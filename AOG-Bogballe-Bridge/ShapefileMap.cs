using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AOGBogballeBridge
{
    public sealed class ShapefileMap : FrameworkElement
    {
        private static readonly Color[] RatePalette =
        {
            Color.FromRgb(0, 100, 0),
            Color.FromRgb(34, 139, 34),
            Color.FromRgb(144, 238, 144),
            Color.FromRgb(255, 255, 0),
            Color.FromRgb(255, 165, 0),
            Color.FromRgb(255, 0, 0),
            Color.FromRgb(139, 0, 0)
        };

        private DrawingGroup? _baseMapDrawing;
        private Size _baseMapSize;
        private Func<Point, Point>? _projectCoordinates;

        public static readonly DependencyProperty FeaturesProperty = DependencyProperty.Register(
            nameof(Features),
            typeof(IReadOnlyList<ShapefileFeature>),
            typeof(ShapefileMap),
            new FrameworkPropertyMetadata(Array.Empty<ShapefileFeature>(), FrameworkPropertyMetadataOptions.AffectsRender, OnBaseMapPropertyChanged));

        public static readonly DependencyProperty ValueFieldProperty = DependencyProperty.Register(
            nameof(ValueField),
            typeof(string),
            typeof(ShapefileMap),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBaseMapPropertyChanged));

        public static readonly DependencyProperty IsGeographicProperty = DependencyProperty.Register(
            nameof(IsGeographic),
            typeof(bool),
            typeof(ShapefileMap),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnBaseMapPropertyChanged));

        public static readonly DependencyProperty GpsPositionValidProperty = DependencyProperty.Register(
            nameof(GpsPositionValid),
            typeof(bool),
            typeof(ShapefileMap),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GpsLatitudeProperty = DependencyProperty.Register(
            nameof(GpsLatitude),
            typeof(double),
            typeof(ShapefileMap),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GpsLongitudeProperty = DependencyProperty.Register(
            nameof(GpsLongitude),
            typeof(double),
            typeof(ShapefileMap),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ToolbarPositionValidProperty = DependencyProperty.Register(
            nameof(ToolbarPositionValid), typeof(bool), typeof(ShapefileMap),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ToolbarLatitudeProperty = DependencyProperty.Register(
            nameof(ToolbarLatitude), typeof(double), typeof(ShapefileMap),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ToolbarLongitudeProperty = DependencyProperty.Register(
            nameof(ToolbarLongitude), typeof(double), typeof(ShapefileMap),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ToolHeadingDegreesProperty = DependencyProperty.Register(
            nameof(ToolHeadingDegrees), typeof(double), typeof(ShapefileMap),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MachineWidthMetersProperty = DependencyProperty.Register(
            nameof(MachineWidthMeters), typeof(double), typeof(ShapefileMap),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SectionWidthsMetersProperty = DependencyProperty.Register(
            nameof(SectionWidthsMeters), typeof(IReadOnlyList<double>), typeof(ShapefileMap),
            new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<ShapefileFeature> Features
        {
            get => (IReadOnlyList<ShapefileFeature>)GetValue(FeaturesProperty);
            set => SetValue(FeaturesProperty, value);
        }

        public string? ValueField
        {
            get => (string?)GetValue(ValueFieldProperty);
            set => SetValue(ValueFieldProperty, value);
        }

        public bool IsGeographic
        {
            get => (bool)GetValue(IsGeographicProperty);
            set => SetValue(IsGeographicProperty, value);
        }

        public bool GpsPositionValid
        {
            get => (bool)GetValue(GpsPositionValidProperty);
            set => SetValue(GpsPositionValidProperty, value);
        }

        public double GpsLatitude
        {
            get => (double)GetValue(GpsLatitudeProperty);
            set => SetValue(GpsLatitudeProperty, value);
        }

        public double GpsLongitude
        {
            get => (double)GetValue(GpsLongitudeProperty);
            set => SetValue(GpsLongitudeProperty, value);
        }

        public bool ToolbarPositionValid
        {
            get => (bool)GetValue(ToolbarPositionValidProperty);
            set => SetValue(ToolbarPositionValidProperty, value);
        }

        public double ToolbarLatitude
        {
            get => (double)GetValue(ToolbarLatitudeProperty);
            set => SetValue(ToolbarLatitudeProperty, value);
        }

        public double ToolbarLongitude
        {
            get => (double)GetValue(ToolbarLongitudeProperty);
            set => SetValue(ToolbarLongitudeProperty, value);
        }

        public double ToolHeadingDegrees
        {
            get => (double)GetValue(ToolHeadingDegreesProperty);
            set => SetValue(ToolHeadingDegreesProperty, value);
        }

        public double MachineWidthMeters
        {
            get => (double)GetValue(MachineWidthMetersProperty);
            set => SetValue(MachineWidthMetersProperty, value);
        }

        public IReadOnlyList<double> SectionWidthsMeters
        {
            get => (IReadOnlyList<double>)GetValue(SectionWidthsMetersProperty);
            set => SetValue(SectionWidthsMetersProperty, value);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            EnsureBaseMap();
            if (_baseMapDrawing == null)
                return;

            drawingContext.DrawDrawing(_baseMapDrawing);
            if (!IsGeographic || _projectCoordinates == null)
                return;

            if (GpsPositionValid)
            {
                Point location = _projectCoordinates(new Point(GpsLongitude, GpsLatitude));
                if (new Rect(RenderSize).Contains(location))
                    drawingContext.DrawEllipse(Brushes.Red, new Pen(Brushes.White, 2), location, 7, 7);
            }

            if (ToolbarPositionValid && MachineWidthMeters > 0 && SectionWidthsMeters.Count > 0)
                DrawToolbar(drawingContext, _projectCoordinates);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            _baseMapDrawing = null;
            _projectCoordinates = null;
            base.OnRenderSizeChanged(sizeInfo);
        }

        private static void OnBaseMapPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            ShapefileMap map = (ShapefileMap)dependencyObject;
            map._baseMapDrawing = null;
            map._projectCoordinates = null;
        }

        private void EnsureBaseMap()
        {
            if (_baseMapDrawing != null && _baseMapSize == RenderSize)
                return;

            _projectCoordinates = null;
            DrawingGroup mapDrawing = new();
            using (DrawingContext drawingContext = mapDrawing.Open())
            {
                drawingContext.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
                Point[] points = Features
                    .SelectMany(feature => feature.Parts)
                    .SelectMany(part => part)
                    .ToArray();
                if (points.Length > 0 && ActualWidth > 0 && ActualHeight > 0)
                {
                    double centerLongitude = (points.Min(point => point.X) + points.Max(point => point.X)) / 2;
                    double centerLatitude = (points.Min(point => point.Y) + points.Max(point => point.Y)) / 2;
                    double latitudeScale = Math.Cos(centerLatitude * Math.PI / 180);
                    Point ToMap(Point point) => IsGeographic
                        ? new Point(
                            6_378_137 * (point.X - centerLongitude) * Math.PI / 180 * latitudeScale,
                            6_378_137 * (point.Y - centerLatitude) * Math.PI / 180)
                        : point;

                    Point[] mapPoints = points.Select(ToMap).ToArray();
                    double minX = mapPoints.Min(point => point.X);
                    double maxX = mapPoints.Max(point => point.X);
                    double minY = mapPoints.Min(point => point.Y);
                    double maxY = mapPoints.Max(point => point.Y);
                    double spanX = Math.Max(maxX - minX, 1e-12);
                    double spanY = Math.Max(maxY - minY, 1e-12);
                    const double margin = 20;
                    double scale = Math.Min(
                        Math.Max(0, ActualWidth - margin * 2) / spanX,
                        Math.Max(0, ActualHeight - margin * 2) / spanY);
                    if (double.IsFinite(scale) && scale > 0)
                    {
                        double contentWidth = spanX * scale;
                        double contentHeight = spanY * scale;
                        double offsetX = (ActualWidth - contentWidth) / 2 - minX * scale;
                        double offsetY = (ActualHeight - contentHeight) / 2 + maxY * scale;
                        Point Project(Point point)
                        {
                            Point mapPoint = ToMap(point);
                            return new Point(offsetX + mapPoint.X * scale, offsetY - mapPoint.Y * scale);
                        }

                        _projectCoordinates = Project;
                        double[]? values = ValueField == null
                            ? null
                            : Features.Select(feature => feature.Attributes.TryGetValue(ValueField, out string? text) &&
                                                          double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                                                          double.IsFinite(value)
                                ? (double?)value
                                : null).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
                        double minValue = values is { Length: > 0 } ? values.Min() : 0;
                        double maxValue = values is { Length: > 0 } ? values.Max() : 0;

                        foreach (ShapefileFeature feature in Features)
                        {
                            Brush fill = GetFillBrush(feature, minValue, maxValue);
                            Pen outline = new(Brushes.SlateGray, 1);
                            switch (feature.GeometryType)
                            {
                                case ShapefileGeometryType.Polygon:
                                    drawingContext.DrawGeometry(fill, outline, CreateGeometry(feature.Parts, Project, close: true));
                                    break;
                                case ShapefileGeometryType.Polyline:
                                    drawingContext.DrawGeometry(null, outline, CreateGeometry(feature.Parts, Project, close: false));
                                    break;
                                case ShapefileGeometryType.Point:
                                case ShapefileGeometryType.MultiPoint:
                                    foreach (Point point in feature.Parts.SelectMany(part => part))
                                        drawingContext.DrawEllipse(fill, outline, Project(point), 3.5, 3.5);
                                    break;
                            }
                        }

                        if (ValueField != null && values is { Length: > 0 })
                            DrawLegend(drawingContext, minValue, maxValue);
                    }
                }
            }

            _baseMapDrawing = mapDrawing;
            _baseMapSize = RenderSize;
        }

        private void DrawToolbar(DrawingContext drawingContext, Func<Point, Point> project)
        {
            Point center = new(ToolbarLongitude, ToolbarLatitude);
            double leftBearing = ToolHeadingDegrees - 90;
            Point start = MovePointByBearing(center, leftBearing, MachineWidthMeters / 2);
            Point end = MovePointByBearing(center, leftBearing + 180, MachineWidthMeters / 2);
            Point projectedStart = project(start);
            Point projectedEnd = project(end);
            drawingContext.DrawLine(new Pen(Brushes.White, 6), projectedStart, projectedEnd);
            drawingContext.DrawLine(new Pen(Brushes.OrangeRed, 3), projectedStart, projectedEnd);

            double distanceFromLeft = 0;
            foreach (double sectionWidth in SectionWidthsMeters)
            {
                distanceFromLeft += sectionWidth;
                if (distanceFromLeft >= MachineWidthMeters - 0.001)
                    break;

                Point boundary = MovePointByBearing(center, leftBearing, MachineWidthMeters / 2 - distanceFromLeft);
                Point tickStart = MovePointByBearing(boundary, ToolHeadingDegrees, 0.75);
                Point tickEnd = MovePointByBearing(boundary, ToolHeadingDegrees + 180, 0.75);
                drawingContext.DrawLine(new Pen(Brushes.White, 4), project(tickStart), project(tickEnd));
                drawingContext.DrawLine(new Pen(Brushes.DarkSlateGray, 1.5), project(tickStart), project(tickEnd));
            }
        }

        private static Point MovePointByBearing(Point position, double bearingDegrees, double distanceMeters)
        {
            static double ToRadians(double degrees) => degrees * Math.PI / 180;
            double bearing = ToRadians(bearingDegrees);
            double angularDistance = distanceMeters / 6_371_000;
            double latitude1 = ToRadians(position.Y);
            double longitude1 = ToRadians(position.X);
            double latitude2 = Math.Asin(
                Math.Sin(latitude1) * Math.Cos(angularDistance) +
                Math.Cos(latitude1) * Math.Sin(angularDistance) * Math.Cos(bearing));
            double longitude2 = longitude1 + Math.Atan2(
                Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitude1),
                Math.Cos(angularDistance) - Math.Sin(latitude1) * Math.Sin(latitude2));
            return new Point(longitude2 * 180 / Math.PI, latitude2 * 180 / Math.PI);
        }

        private void DrawLegend(DrawingContext drawingContext, double minValue, double maxValue)
        {
            const double height = 58;
            double width = Math.Min(220, ActualWidth - 24);
            if (width <= 0)
                return;

            double left = ActualWidth - width - 12;
            const double top = 12;
            Rect background = new(left, top, width, height);
            drawingContext.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                new Pen(Brushes.SlateGray, 1),
                background,
                4,
                4);

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText fieldLabel = new(
                $"DBF field: {ValueField}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                Brushes.Black,
                pixelsPerDip)
            {
                MaxTextWidth = width - 16,
                Trimming = TextTrimming.CharacterEllipsis
            };
            drawingContext.DrawText(fieldLabel, new Point(left + 8, top + 5));

            Rect colorRange = new(left + 8, top + 26, width - 16, 12);
            LinearGradientBrush gradient = CreateRateGradientBrush();
            drawingContext.DrawRectangle(gradient, null, colorRange);

            FormattedText minimum = new(
                minValue.ToString("G6", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                Brushes.Black,
                pixelsPerDip);
            FormattedText maximum = new(
                maxValue.ToString("G6", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                Brushes.Black,
                pixelsPerDip);
            drawingContext.DrawText(minimum, new Point(left + 8, top + 40));
            drawingContext.DrawText(maximum, new Point(left + width - 8 - maximum.Width, top + 40));
        }

        private Brush GetFillBrush(ShapefileFeature feature, double minValue, double maxValue)
        {
            if (ValueField == null || !feature.Attributes.TryGetValue(ValueField, out string? text) ||
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                !double.IsFinite(value))
                return new SolidColorBrush(Color.FromRgb(115, 177, 216));

            double fraction = maxValue > minValue ? Math.Clamp((value - minValue) / (maxValue - minValue), 0, 1) : 0.5;
            return new SolidColorBrush(GetRateColor(fraction));
        }

        private static LinearGradientBrush CreateRateGradientBrush()
        {
            LinearGradientBrush gradient = new()
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            for (int i = 0; i < RatePalette.Length; i++)
                gradient.GradientStops.Add(new GradientStop(RatePalette[i], (double)i / (RatePalette.Length - 1)));
            gradient.Freeze();
            return gradient;
        }

        private static Color GetRateColor(double fraction)
        {
            double scaledPosition = Math.Clamp(fraction, 0, 1) * (RatePalette.Length - 1);
            int lowerIndex = Math.Min((int)scaledPosition, RatePalette.Length - 2);
            double blend = scaledPosition - lowerIndex;
            Color lower = RatePalette[lowerIndex];
            Color upper = RatePalette[lowerIndex + 1];
            return Color.FromRgb(
                (byte)Math.Round(lower.R + (upper.R - lower.R) * blend),
                (byte)Math.Round(lower.G + (upper.G - lower.G) * blend),
                (byte)Math.Round(lower.B + (upper.B - lower.B) * blend));
        }

        private static StreamGeometry CreateGeometry(
            IReadOnlyList<IReadOnlyList<Point>> parts,
            Func<Point, Point> project,
            bool close)
        {
            StreamGeometry geometry = new() { FillRule = FillRule.EvenOdd };
            using (StreamGeometryContext context = geometry.Open())
            {
                foreach (IReadOnlyList<Point> part in parts)
                {
                    if (part.Count == 0)
                        continue;

                    context.BeginFigure(project(part[0]), close, close);
                    if (part.Count > 1)
                        context.PolyLineTo(part.Skip(1).Select(project).ToArray(), true, false);
                }
            }

            geometry.Freeze();
            return geometry;
        }
    }
}