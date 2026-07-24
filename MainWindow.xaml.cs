using System.Windows;
using System.Windows.Input;
using DynoCommandPC.ViewModel;

namespace DynoCommandPC;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += (_, _) => _vm.TryAutoConnectAtStartup();
        Closed += (_, _) => _vm.Shutdown();
    }

    // Set Bed is a dead-man: the bed drives only while the button is held. Capture
    // the mouse on press so release is caught even if the pointer slides off, and
    // treat MouseLeave as a release too so the bed can never latch on.
    private void BedButton_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string which)
        {
            _vm.BedMoveStart(which);
            (sender as UIElement)?.CaptureMouse();
        }
    }

    private void BedButton_Up(object sender, MouseEventArgs e)
    {
        _vm.BedMoveStop();
        (sender as UIElement)?.ReleaseMouseCapture();
    }
}
