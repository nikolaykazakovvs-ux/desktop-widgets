using System.Windows;
using System.Windows.Markup;

namespace LimitsWidget
{
    // Плоское контекстное меню в цветах темы (без подменю: параметры переключаются по кругу).
    static class MenuStyles
    {
        const string NS = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

        const string MenuXaml =
@"<Style %NS% TargetType='ContextMenu'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='SnapsToDevicePixels' Value='True'/>
  <Setter Property='HasDropShadow' Value='True'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ContextMenu'>
        <Border Background='%BG%' BorderBrush='%BORDER%' BorderThickness='1' CornerRadius='8' Padding='0,4' Margin='0,0,10,10'>
          <Border.Effect><DropShadowEffect BlurRadius='12' ShadowDepth='3' Direction='270' Opacity='0.28'/></Border.Effect>
          <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
        </Border>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        const string ItemXaml =
@"<Style %NS% TargetType='MenuItem'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Foreground' Value='%TEXT%'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='MenuItem'>
        <Border x:Name='bd' Background='Transparent' CornerRadius='5' Padding='8,6,16,6' Margin='4,0'>
          <ContentPresenter ContentSource='Header' RecognizesAccessKey='False'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'>
            <Setter TargetName='bd' Property='Background' Value='%HOVER%'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        const string SeparatorXaml =
@"<Style %NS% TargetType='Separator'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Separator'>
        <Border Height='1' Margin='10,4' Background='%SEP%'/>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        public static Style Menu(Theme t) { return Parse(MenuXaml, t); }
        public static Style Item(Theme t) { return Parse(ItemXaml, t); }
        public static Style Separator(Theme t) { return Parse(SeparatorXaml, t); }

        static Style Parse(string xaml, Theme t)
        {
            string s = xaml.Replace("%NS%", NS)
                .Replace("%BG%", Theme.Hex(t.MenuBg.Color))
                .Replace("%BORDER%", Theme.Hex(t.CardBorder.Color))
                .Replace("%TEXT%", Theme.Hex(t.Text.Color))
                .Replace("%HOVER%", Theme.Hex(t.Hover.Color))
                .Replace("%SEP%", Theme.Hex(t.Separator.Color));
            return (Style)XamlReader.Parse(s);
        }
    }
}
