using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WhatIfGa.Controls;

/// <summary>A lightweight local chart with point inspection, keyboard navigation and optional zoom.</summary>
public sealed class UsageChart : Control
{
    public static readonly StyledProperty<IEnumerable<ChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<UsageChart, IEnumerable<ChartPoint>?>(nameof(Points));
    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<UsageChart, string>(nameof(Unit), "kWh");
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#19816B"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#5B7DB8"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#74828A"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#E8EEEB"));
    private static readonly Typeface Font = new("Inter, Segoe UI, sans-serif");
    private INotifyCollectionChanged? _observable;
    private int _hover = -1;
    private int _start;
    private int _count = int.MaxValue;
    private ChartPoint[] _visible = Array.Empty<ChartPoint>();
    private Rect _plot;

    public IEnumerable<ChartPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    static UsageChart() => AffectsRender<UsageChart>(PointsProperty, UnitProperty);

    public UsageChart()
    {
        Focusable = true;
        MinHeight = 230;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "Usage comparison chart. Hover or use left and right arrows to inspect values. Control plus mouse wheel zooms; Home resets.");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PointsProperty) return;
        if (_observable != null) _observable.CollectionChanged -= PointsChanged;
        _observable = Points as INotifyCollectionChanged;
        if (_observable != null) _observable.CollectionChanged += PointsChanged;
        Reset();
    }

    private void PointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Reset();
    private void Reset() { _start = 0; _count = int.MaxValue; _hover = -1; InvalidateVisual(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (Bounds.Width < 120 || Bounds.Height < 120) return;
        _plot = new Rect(56, 22, Math.Max(1, Bounds.Width - 74), Math.Max(1, Bounds.Height - 66));
        var all = Points?.ToArray() ?? Array.Empty<ChartPoint>();
        _start = Math.Clamp(_start, 0, Math.Max(0, all.Length - 1));
        _visible = all.Skip(_start).Take(_count).ToArray();
        if (_visible.Length == 0)
        {
            DrawText(context, "No values to display.", new Point(24, Bounds.Height / 2 - 10), Muted, 13);
            return;
        }
        var maxValue = _visible.Max(p => Math.Max(p.OriginalAvailable ? p.Original : 0, p.Modified ?? 0));
        var max = NiceMaximum(Math.Max(1, maxValue));
        for (var i = 0; i <= 4; i++)
        {
            var y = _plot.Bottom - i * _plot.Height / 4;
            context.DrawLine(new Pen(GridBrush), new Point(_plot.X, y), new Point(_plot.Right, y));
            var label = Format(max * i / 4, false);
            var text = Text(label, Muted, 11);
            context.DrawText(text, new Point(_plot.X - text.Width - 12, y - text.Height / 2));
        }
        DrawSeries(context, _visible.Select(p => p.OriginalAvailable ? (double?)p.Original : null).ToArray(), max, Green, true);
        if (_visible.Any(p => p.Modified.HasValue))
            DrawSeries(context, _visible.Select(p => p.Modified).ToArray(), max, Blue, false);
        var step = Math.Max(1, (int)Math.Ceiling(_visible.Length / Math.Max(2, _plot.Width / 58)));
        for (var i = 0; i < _visible.Length; i += step)
        {
            var label = Text(_visible[i].Label, Muted, 11);
            context.DrawText(label, new Point(X(i) - label.Width / 2, _plot.Bottom + 13));
        }
        if (_hover >= 0 && _hover < _visible.Length)
        {
            var point = _visible[_hover];
            var x = X(_hover);
            context.DrawLine(new Pen(Muted, 1, new DashStyle(new[] { 3d, 3d }, 0)), new Point(x, _plot.Y), new Point(x, _plot.Bottom));
            if (point.OriginalAvailable)
                context.DrawEllipse(Green, new Pen(Brushes.White, 2), new Point(x, Y(point.Original, max)), 5, 5);
            if (point.Modified.HasValue)
                context.DrawEllipse(Blue, new Pen(Brushes.White, 2), new Point(x, Y(point.Modified.Value, max)), 5, 5);
            var first = Text(point.Label + "  ·  Original: " + (point.OriginalAvailable ? Format(point.Original) : "no reading"), Brushes.White, 12);
            var second = _visible.Any(p => p.Modified.HasValue)
                ? Text("Scenario: " + (point.Modified.HasValue ? Format(point.Modified.Value) : "no reading"), new SolidColorBrush(Color.Parse("#B9D6FF")), 12) : null;
            var width = Math.Max(first.Width, second?.Width ?? 0) + 24;
            var height = second == null ? 34 : 54;
            var left = Math.Clamp(x - width / 2, 1, Math.Max(1, Bounds.Width - width - 1));
            var top = Math.Max(1, _plot.Y + 4);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#20343D")), null, new Rect(left, top, width, height), 6, 6);
            context.DrawText(first, new Point(left + 12, top + 8));
            if (second != null) context.DrawText(second, new Point(left + 12, top + 29));
        }
    }

    private void DrawSeries(DrawingContext context, double?[] values, double max, IBrush stroke, bool area)
    {
        // Each contiguous run has its own line and area. Missing readings remain gaps,
        // including when a scenario introduces a destination hour absent from the baseline.
        var index = 0;
        while (index < values.Length)
        {
            while (index < values.Length && !values[index].HasValue) index++;
            if (index == values.Length) break;
            var start = index;
            while (index + 1 < values.Length && values[index + 1].HasValue) index++;
            DrawRun(context, values, start, index, max, stroke, area);
            index++;
        }
    }

    private void DrawRun(DrawingContext context, double?[] values, int start, int end, double max, IBrush stroke, bool area)
    {
        if (start == end)
        {
            context.DrawEllipse(stroke, null, new Point(X(start), Y(values[start]!.Value, max)), 4, 4);
            return;
        }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(X(start), Y(values[start]!.Value, max)), false);
            for (var i = start + 1; i <= end; i++) path.LineTo(new Point(X(i), Y(values[i]!.Value, max)));
            path.EndFigure(false);
        }
        if (area)
        {
            var fill = new StreamGeometry();
            using (var path = fill.Open())
            {
                path.BeginFigure(new Point(X(start), _plot.Bottom), true);
                for (var i = start; i <= end; i++) path.LineTo(new Point(X(i), Y(values[i]!.Value, max)));
                path.LineTo(new Point(X(end), _plot.Bottom));
                path.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#E5F2EC")), null, fill);
        }
        context.DrawGeometry(null, new Pen(stroke, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        if (values.Length <= 24)
            for (var i = start; i <= end; i++) context.DrawEllipse(Brushes.White, new Pen(stroke, 2), new Point(X(i), Y(values[i]!.Value, max)), 3, 3);
    }

    private double X(int index) => _visible.Length <= 1 ? _plot.Center.X : _plot.X + index * _plot.Width / (_visible.Length - 1);
    private double Y(double value, double max) => _plot.Bottom - Math.Clamp(value / max, 0, 1) * _plot.Height;
    private static double NiceMaximum(double value)
    {
        var scale = Math.Pow(10, Math.Floor(Math.Log10(value)));
        return Math.Ceiling(value / scale / .5) * .5 * scale;
    }
    private string Format(double value, bool suffix = true) => Unit == "$"
        ? "$" + value.ToString(suffix ? "N2" : "N0", CultureInfo.GetCultureInfo("en-US"))
        : value.ToString(suffix ? "N1" : "N0", CultureInfo.GetCultureInfo("en-US")) + (suffix ? " kWh" : "");
    private static FormattedText Text(string text, IBrush brush, double size) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush);
    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush, double size) => context.DrawText(Text(text, brush, size), point);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _hover = _visible.Length == 0 || !_plot.Contains(position) ? -1
            : Math.Clamp((int)Math.Round((position.X - _plot.X) / _plot.Width * (_visible.Length - 1)), 0, _visible.Length - 1);
        InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = -1; InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.ClickCount == 2) Reset();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Home) { Reset(); e.Handled = true; }
        if (e.Key is Key.Left or Key.Right && _visible.Length > 0)
        {
            _hover = Math.Clamp(_hover + (e.Key == Key.Left ? -1 : 1), 0, _visible.Length - 1);
            InvalidateVisual();
            e.Handled = true;
        }
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var total = Points?.Count() ?? 0;
        if (total < 4) return;
        var current = Math.Min(total, _count);
        var next = Math.Clamp(current + (e.Delta.Y > 0 ? -2 : 2), 3, total);
        var center = _start + (_hover >= 0 ? _hover : current / 2);
        _count = next;
        _start = Math.Clamp(center - next / 2, 0, total - next);
        _hover = -1;
        InvalidateVisual();
        e.Handled = true;
    }
}
