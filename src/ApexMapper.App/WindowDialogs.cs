using System.Windows;
using ApexMapper.App.Model;
using Microsoft.Win32;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace ApexMapper.App;

/// <summary>The view models' questions as dialogs over the main window.</summary>
internal sealed class WindowDialogs(Window owner) : IDialogs
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        var box = new MessageBox
        {
            Owner = owner,
            Title = title,
            Content = message,
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public string? AskSavePath(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json",
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
