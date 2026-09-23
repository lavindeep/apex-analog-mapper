namespace ApexMapper.App.Mvvm;

/// <summary>
/// Lets a command or an event start a task without waiting for it. A task handles the
/// failures it expects; anything else it throws is raised again on the UI thread, where
/// the app's error handler logs it and closes the app, instead of vanishing with the task.
/// </summary>
public static class Background
{
    public static async void Run(Task task) => await task;
}
