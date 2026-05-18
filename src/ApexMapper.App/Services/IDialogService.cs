namespace ApexMapper.App.Services;

/// <summary>Shows modal dialogs for informational messages, errors, and confirmations.</summary>
public interface IDialogService
{
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
    bool Confirm(string title, string message);
}
