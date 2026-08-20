using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PeripheralBatteryDashboard.UI
{
    internal static class UiFactory
    {
        internal static readonly SolidColorBrush WindowBackground = Brush(11, 18, 32);
        internal static readonly SolidColorBrush PanelBackground = Brush(17, 27, 46);
        internal static readonly SolidColorBrush RaisedBackground = Brush(24, 37, 59);
        internal static readonly SolidColorBrush HoverBackground = Brush(33, 49, 75);
        internal static readonly SolidColorBrush BorderBrush = Brush(42, 58, 82);
        internal static readonly SolidColorBrush PrimaryText = Brush(238, 244, 255);
        internal static readonly SolidColorBrush SecondaryText = Brush(155, 170, 194);
        internal static readonly SolidColorBrush MutedText = Brush(104, 120, 145);
        internal static readonly SolidColorBrush Accent = Brush(55, 206, 194);
        internal static readonly SolidColorBrush AccentDark = Brush(25, 99, 95);
        internal static readonly SolidColorBrush Success = Brush(64, 210, 141);
        internal static readonly SolidColorBrush Warning = Brush(245, 183, 66);
        internal static readonly SolidColorBrush Danger = Brush(251, 96, 119);
        internal static readonly SolidColorBrush Offline = Brush(102, 116, 139);

        internal static SolidColorBrush Brush(byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        internal static TextBlock Text(string value, double size, Brush color, FontWeight weight)
        {
            return new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                FontSize = size,
                FontWeight = weight,
                Foreground = color,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        internal static Border Card(UIElement child, Thickness margin)
        {
            return new Border
            {
                Child = child,
                Background = PanelBackground,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Margin = margin,
                Padding = new Thickness(20)
            };
        }

        internal static Button Button(string label, bool primary)
        {
            Button button = new Button
            {
                Content = label,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryText,
                Background = primary ? AccentDark : RaisedBackground,
                BorderBrush = primary ? Accent : BorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(15, 8, 15, 8),
                MinHeight = 36,
                Cursor = Cursors.Hand,
                FocusVisualStyle = null
            };

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            ControlTemplate template = new ControlTemplate(typeof(Button));
            template.VisualTree = border;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, primary ? Brush(30, 125, 119) : HoverBackground));
            template.Triggers.Add(hover);

            Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.78));
            template.Triggers.Add(pressed);

            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);

            button.Template = template;
            return button;
        }

        internal static Style TabItemStyle()
        {
            Style style = new Style(typeof(TabItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, SecondaryText));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 10, 18, 10)));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI, Malgun Gothic")));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "tabBorder";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            ControlTemplate template = new ControlTemplate(typeof(TabItem));
            template.VisualTree = border;
            Trigger selected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, RaisedBackground));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, PrimaryText));
            template.Triggers.Add(selected);
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.ForegroundProperty, PrimaryText));
            template.Triggers.Add(hover);

            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }
    }
}
