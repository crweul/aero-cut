using System.Windows;
using System.Windows.Threading;

namespace AeroCut;

public partial class App : Application
{
    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "AeroCut", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
