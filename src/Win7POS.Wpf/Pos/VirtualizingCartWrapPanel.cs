using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Win7POS.Wpf.Pos
{
    /// <summary>
    /// Pixel-scrolling card rows. Only the viewport plus one row on each side
    /// is realized. Measured row heights preserve wrapping and variable content;
    /// estimates affect the scrollbar only, never the size assigned to a card.
    /// </summary>
    public sealed class VirtualizingCartWrapPanel : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
            nameof(ItemWidth), typeof(double), typeof(VirtualizingCartWrapPanel),
            new FrameworkPropertyMetadata(184d, FrameworkPropertyMetadataOptions.AffectsMeasure),
            value => (double)value > 0 && !double.IsInfinity((double)value) && !double.IsNaN((double)value));
        public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }

        private readonly Dictionary<int, double> _rowHeights = new Dictionary<int, double>();
        private int _columns = 1;
        private int _rowCount;
        private double _estimatedHeight = 350;
        private double _offset;
        private Size _viewport;
        private Size _extent;
        private int _bringIndex = -1;

        protected override Size MeasureOverride(Size availableSize)
        {
            var owner = ItemsControl.GetItemsOwner(this);
            if (owner == null) return new Size();
            var width = double.IsInfinity(availableSize.Width) ? Math.Max(ItemWidth, ActualWidth) : availableSize.Width;
            var height = double.IsInfinity(availableSize.Height) ? Math.Max(1, ScrollOwner?.ActualHeight ?? ActualHeight) : availableSize.Height;
            width = Math.Max(0, width);
            height = Math.Max(0, height);
            var columns = Math.Max(1, (int)(width / ItemWidth));
            if (columns != _columns)
            {
                var anchor = RowAtOffset(_offset) * _columns;
                _columns = columns;
                _rowHeights.Clear();
                _offset = (anchor / _columns) * _estimatedHeight;
            }
            _rowCount = (owner.Items.Count + _columns - 1) / _columns;
            _viewport = new Size(width, height);
            if (_bringIndex >= 0) _offset = RowTop(Math.Min(_rowCount - 1, _bringIndex / _columns));
            UpdateExtent();
            _offset = ClampOffset(_offset);
            var anchorRow = RowAtOffset(_offset);
            var withinRow = _offset - RowTop(anchorRow);
            var firstRow = Math.Max(0, anchorRow - 1);
            var lastRow = Math.Min(_rowCount - 1, RowAtOffset(_offset + height) + 1);
            var first = firstRow * _columns;
            var last = Math.Min(owner.Items.Count - 1, (lastRow + 1) * _columns - 1);
            // Panel initializes its generator on first InternalChildren access.
            // A view can already contain items before its very first measure.
            var children = InternalChildren;
            var generator = ItemContainerGenerator;
            // Recycle before generating so recycled containers are available to
            // the incoming row. The generator, not child position, owns indices.
            for (var childIndex = children.Count - 1; childIndex >= 0; childIndex--)
            {
                var itemIndex = owner.ItemContainerGenerator.IndexFromContainer(InternalChildren[childIndex]);
                if (itemIndex >= first && itemIndex <= last) continue;
                var position = new GeneratorPosition(childIndex, 0);
                if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1);
                else generator.Remove(position, 1);
                RemoveInternalChildRange(childIndex, 1);
            }
            if (last >= first)
            {
                var position = generator.GeneratorPositionFromIndex(first);
                var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
                using (generator.StartAt(position, GeneratorDirection.Forward, true))
                {
                    var rowHeight = 0d;
                    for (var itemIndex = first; itemIndex <= last; itemIndex++, childIndex++)
                    {
                        var child = (UIElement)generator.GenerateNext(out var created);
                        // A recycled container may already be known to the
                        // generator while detached from this panel's visuals.
                        if (created || VisualTreeHelper.GetParent(child) != this)
                        {
                            if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                            else InsertInternalChild(childIndex, child);
                            generator.PrepareItemContainer(child);
                        }
                        child.Measure(new Size(ItemWidth, double.PositiveInfinity));
                        rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                        if ((itemIndex + 1) % _columns == 0 || itemIndex == last)
                        {
                            _rowHeights[itemIndex / _columns] = Math.Max(1, rowHeight);
                            rowHeight = 0;
                        }
                    }
                }
            }
            if (_rowHeights.TryGetValue(anchorRow, out var measured)) _estimatedHeight = measured;
            _offset = RowTop(anchorRow) + Math.Min(withinRow, Math.Max(0, HeightOfRow(anchorRow) - 1));
            UpdateExtent();
            _offset = ClampOffset(_offset);
            if (_bringIndex >= 0)
            {
                var targetRow = _bringIndex / _columns;
                var bottom = RowTop(targetRow) + HeightOfRow(targetRow);
                if (bottom > _offset + height) _offset = ClampOffset(bottom - height);
                _bringIndex = -1;
            }
            ScrollOwner?.InvalidateScrollInfo();
            // A newly measured row can be shorter than its estimate. Repeat a
            // bounded layout pass if the viewport needs additional containers.
            if (last < owner.Items.Count - 1 && RowTop(lastRow + 1) < _offset + height) InvalidateMeasure();
            return _viewport;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var owner = ItemsControl.GetItemsOwner(this);
            foreach (UIElement child in InternalChildren)
            {
                var index = owner.ItemContainerGenerator.IndexFromContainer(child);
                if (index < 0) continue;
                var row = index / _columns;
                child.Arrange(new Rect((index % _columns) * ItemWidth, RowTop(row) - _offset, ItemWidth, HeightOfRow(row)));
            }
            return finalSize;
        }

        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            base.OnItemsChanged(sender, args);
            // Structural changes invalidate row/index mapping. Ordinary quantity
            // and price notifications keep both data and realized containers.
            ItemContainerGenerator.RemoveAll();
            RemoveInternalChildRange(0, InternalChildren.Count);
            _rowHeights.Clear();
            InvalidateMeasure();
        }

        protected override void BringIndexIntoView(int index)
        {
            var owner = ItemsControl.GetItemsOwner(this);
            if (owner == null || index < 0 || index >= owner.Items.Count) return;
            _bringIndex = index;
            InvalidateMeasure();
        }

        private double HeightOfRow(int row) => _rowHeights.TryGetValue(row, out var height) ? height : _estimatedHeight;
        private double RowTop(int row)
        {
            var top = Math.Max(0, row) * _estimatedHeight;
            foreach (var pair in _rowHeights)
                if (pair.Key < row) top += pair.Value - _estimatedHeight;
            return top;
        }
        private int RowAtOffset(double offset)
        {
            var top = 0d;
            for (var row = 0; row < _rowCount; row++)
            {
                top += HeightOfRow(row);
                if (top > offset) return row;
            }
            return Math.Max(0, _rowCount - 1);
        }
        private void UpdateExtent() => _extent = new Size(_viewport.Width, RowTop(_rowCount));
        private double ClampOffset(double value) => Math.Max(0, Math.Min(value, Math.Max(0, ExtentHeight - ViewportHeight)));

        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; }
        public double ExtentWidth => _extent.Width;
        public double ExtentHeight => _extent.Height;
        public double ViewportWidth => _viewport.Width;
        public double ViewportHeight => _viewport.Height;
        public double HorizontalOffset => 0;
        public double VerticalOffset => _offset;
        public ScrollViewer ScrollOwner { get; set; }
        public void LineUp() => SetVerticalOffset(_offset - 32);
        public void LineDown() => SetVerticalOffset(_offset + 32);
        public void MouseWheelUp() => SetVerticalOffset(_offset - 96);
        public void MouseWheelDown() => SetVerticalOffset(_offset + 96);
        public void PageUp() => SetVerticalOffset(_offset - ViewportHeight);
        public void PageDown() => SetVerticalOffset(_offset + ViewportHeight);
        public void LineLeft() { }
        public void LineRight() { }
        public void MouseWheelLeft() { }
        public void MouseWheelRight() { }
        public void PageLeft() { }
        public void PageRight() { }
        public void SetHorizontalOffset(double offset) { }
        public void SetVerticalOffset(double offset)
        {
            if (double.IsNaN(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
            var value = ClampOffset(offset);
            if (Math.Abs(value - _offset) < .01) return;
            _offset = value;
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }
        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            if (visual == null || rectangle.IsEmpty) return Rect.Empty;
            DependencyObject child = visual;
            while (child != null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
            if (child == null) return Rect.Empty;
            var index = ItemsControl.GetItemsOwner(this).ItemContainerGenerator.IndexFromContainer(child);
            if (index < 0) return Rect.Empty;
            // A focus/accessibility request can target a small part of a card
            // taller than the viewport. Scroll that rectangle, not the whole
            // row, or bringing its top into view would hide it again.
            var visible = visual.TransformToAncestor(this).TransformBounds(rectangle);
            var previousOffset = _offset;
            var top = visible.Top + previousOffset;
            if (top < _offset) SetVerticalOffset(top);
            else if (top + visible.Height > _offset + ViewportHeight)
                SetVerticalOffset(top + visible.Height - ViewportHeight);
            visible.Offset(0, previousOffset - _offset);
            visible.Intersect(new Rect(_viewport));
            return visible;
        }
    }
}
