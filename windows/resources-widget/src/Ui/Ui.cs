using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace ResourcesWidget
{
    // Значки Segoe Fluent Icons.
    static class Glyph
    {
        public const string Cpu = "", Memory = "", Gpu = "", Disk = "", Network = "", Processes = "";
        public const string Pin = "", More = "", Minimize = "", Down = "", Up = "";
        public const string ThemeAuto = "", ThemeLight = "", ThemeDark = "";
    }

    static class Ui
    {
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily DisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly GridLength Auto = GridLength.Auto;
        public static readonly GridLength Star = new GridLength(1, GridUnitType.Star);
        static Style iconButtonStyle, chipButtonStyle;

        public static GridLength Px(double v)
        {
            return new GridLength(v);
        }

        public static Grid Columns(params GridLength[] widths)
        {
            var g = new Grid();
            foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            return g;
        }

        public static T At<T>(T e, int column) where T : UIElement
        {
            Grid.SetColumn(e, column);
            return e;
        }

        public static TextBlock Text(string text, double size, Brush brush)
        {
            return new TextBlock { Text = text ?? "", FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
        }

        // Числа — цифрами одинаковой ширины, чтобы значения не прыгали при обновлении.
        public static TextBlock Number(double size, Brush brush, bool bold)
        {
            var tb = Text("", size, brush);
            if (bold) tb.FontWeight = FontWeights.SemiBold;
            Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            return tb;
        }

        public static TextBlock Small(Brush brush, Thickness margin)
        {
            var tb = Number(10.5, brush, false);
            tb.Margin = margin;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            return tb;
        }

        public static Border Chip(string text, Theme t)
        {
            var chip = new Border { Background = t.ChipBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            chip.Child = new TextBlock { Text = text ?? "", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = t.ChipText, TextTrimming = TextTrimming.CharacterEllipsis };
            return chip;
        }

        public static Border Badge(string glyph, Color accent)
        {
            var b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = Theme.B(Theme.Alpha(accent, 0.2)), VerticalAlignment = VerticalAlignment.Center };
            b.Child = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12, Foreground = Theme.B(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return b;
        }

        public static ToolTip Tip(FrameworkElement e, object content, Style style)
        {
            var tip = new ToolTip { Content = content, Style = style };
            e.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(e, 350);
            ToolTipService.SetShowDuration(e, 60000);
            return tip;
        }

        public static Button IconButton(string glyph, string tip, Theme t, Style tipStyle, double box, double fontSize)
        {
            var b = new Button();
            b.Style = IconButtonStyle();
            b.Width = box;
            b.Height = box;
            b.Padding = new Thickness(0);
            b.Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = fontSize, Foreground = t.Text2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Tip(b, tip, tipStyle);
            System.Windows.Automation.AutomationProperties.SetName(b, tip);   // имя для экранного диктора и UI Automation
            return b;
        }

        // Переключатель-пометка (сортировка процессов).
        public static Button ChipButton(string text)
        {
            var b = new Button();
            b.Style = ChipButtonStyle();
            b.Content = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold };
            b.HorizontalAlignment = HorizontalAlignment.Right;
            b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }

        static Style IconButtonStyle()
        {
            if (iconButtonStyle != null) return iconButtonStyle;
            iconButtonStyle = (Style)XamlReader.Parse(
@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='BorderThickness' Value='0'/>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='bd' Property='Background' Value='{DynamicResource RW.Hover}'/>
          </Trigger>
          <Trigger Property='IsPressed' Value='True'>
            <Setter TargetName='bd' Property='Opacity' Value='0.6'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>");
            return iconButtonStyle;
        }

        static Style ChipButtonStyle()
        {
            if (chipButtonStyle != null) return chipButtonStyle;
            chipButtonStyle = (Style)XamlReader.Parse(
@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='BorderThickness' Value='0'/>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='4' Padding='6,1,6,2'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='bd' Property='Background' Value='{DynamicResource RW.Hover}'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>");
            return chipButtonStyle;
        }
    }
}
