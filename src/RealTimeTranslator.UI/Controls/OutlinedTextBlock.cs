using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RealTimeTranslator.UI.Controls
{
    public class OutlinedTextBlock : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register("FontFamily", typeof(FontFamily), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register("FontSize", typeof(double), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register("FontWeight", typeof(FontWeight), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontWeight, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontStyleProperty =
            DependencyProperty.Register("FontStyle", typeof(FontStyle), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontStyle, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnFormattedTextInvalidated));

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register("Fill", typeof(Brush), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register("Stroke", typeof(Brush), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register("StrokeThickness", typeof(double), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty TextAlignmentProperty = 
            DependencyProperty.Register("TextAlignment", typeof(TextAlignment), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnFormattedTextInvalidated));
        
        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register("TextWrapping", typeof(TextWrapping), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(TextWrapping.NoWrap, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnFormattedTextInvalidated));

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public FontFamily FontFamily
        {
            get { return (FontFamily)GetValue(FontFamilyProperty); }
            set { SetValue(FontFamilyProperty, value); }
        }

        public double FontSize
        {
            get { return (double)GetValue(FontSizeProperty); }
            set { SetValue(FontSizeProperty, value); }
        }

        public FontWeight FontWeight
        {
            get { return (FontWeight)GetValue(FontWeightProperty); }
            set { SetValue(FontWeightProperty, value); }
        }

        public FontStyle FontStyle
        {
            get { return (FontStyle)GetValue(FontStyleProperty); }
            set { SetValue(FontStyleProperty, value); }
        }

        public Brush Fill
        {
            get { return (Brush)GetValue(FillProperty); }
            set { SetValue(FillProperty, value); }
        }

        public Brush Stroke
        {
            get { return (Brush)GetValue(StrokeProperty); }
            set { SetValue(StrokeProperty, value); }
        }

        public double StrokeThickness
        {
            get { return (double)GetValue(StrokeThicknessProperty); }
            set { SetValue(StrokeThicknessProperty, value); }
        }

        public TextAlignment TextAlignment
        {
            get { return (TextAlignment)GetValue(TextAlignmentProperty); }
            set { SetValue(TextAlignmentProperty, value); }
        }

        public TextWrapping TextWrapping
        {
            get { return (TextWrapping)GetValue(TextWrappingProperty); }
            set { SetValue(TextWrappingProperty, value); }
        }

        private FormattedText? _formattedText;

        private static void OnFormattedTextInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((OutlinedTextBlock)d)._formattedText = null;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_formattedText == null) return;

            var geometry = _formattedText.BuildGeometry(new Point(StrokeThickness / 2, StrokeThickness / 2));
            
            if (Stroke != null && StrokeThickness > 0)
            {
                drawingContext.DrawGeometry(null, new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round }, geometry);
            }

            drawingContext.DrawGeometry(Fill, null, geometry);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            EnsureFormattedText(availableSize.Width);
            
            if (_formattedText == null)
                return new Size(0, 0);

            return new Size(
                _formattedText.Width + StrokeThickness, 
                _formattedText.Height + StrokeThickness);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            EnsureFormattedText(finalSize.Width);
            return finalSize;
        }

        private void EnsureFormattedText(double availableWidth)
        {
            if (string.IsNullOrEmpty(Text))
            {
                _formattedText = null;
                return;
            }

            // widthCheck: FormattedTextの再生成が必要かどうか判断するロジックは少し複雑なので、
            // 簡略化のために毎回生成するか、MaxTextWidthだけ更新する。
            // しかしFormattedTextはイミュータブルではないが、コンストラクタで指定した一部のプロパティは変更不可。
            // ここではシンプルに毎回作り直す（頻繁に変更されるわけではないため）。
            
            _formattedText = new FormattedText(
                Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal),
                FontSize,
                Brushes.Black, // FillブラシはOnRenderで使用
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            _formattedText.TextAlignment = TextAlignment;
            
            if (TextWrapping == TextWrapping.Wrap && !double.IsPositiveInfinity(availableWidth))
            {
                _formattedText.MaxTextWidth = Math.Max(0, availableWidth - StrokeThickness);
            }
        }
    }
}
