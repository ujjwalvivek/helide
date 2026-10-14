using System.Windows;
using System.Windows.Automation;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Helide;

// Icon buttons say what they are to a screen reader: the words their tooltip shows.
public partial class MainWindow
{
    private static bool _buttonsNamed;

    // Every button takes its tooltip as its name the moment the tooltip is set -- in the
    // window, in a tab made later, in a menu built as it opens -- unless it has a name of
    // its own. Hooked once, before the first button exists.
    private static void NameIconButtons()
    {
        if (_buttonsNamed)
            return;

        _buttonsNamed = true;
        try
        {
            FrameworkElement.ToolTipProperty.OverrideMetadata(
                typeof(ButtonBase),
                new FrameworkPropertyMetadata(null, (element, change) =>
                {
                    if (change.NewValue is string tip
                        && string.IsNullOrEmpty(AutomationProperties.GetName(element)))
                        AutomationProperties.SetName(element, tip);
                }));
        }
        catch (ArgumentException)
        {
            // Already hooked for buttons; nothing to add.
        }
    }
}
