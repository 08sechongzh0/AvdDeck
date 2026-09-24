using System.Windows;
using System.Windows.Media;

namespace AvdDeck.Dialogs;

public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirm, bool danger)
    {
        InitializeComponent();
        TitleText.Text = title; MessageText.Text = message; ConfirmButton.Content = confirm;
        if (danger) ConfirmButton.Background = new SolidColorBrush(Color.FromRgb(207, 70, 66));
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }
    public static bool Ask(Window owner, string title, string message, string confirm, bool danger = false) => new ConfirmDialog(title, message, confirm, danger) { Owner = owner }.ShowDialog() == true;
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
