using System.Windows;
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
}
