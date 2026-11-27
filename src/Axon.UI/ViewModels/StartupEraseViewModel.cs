using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.UI.Application;
using Axon.UI.Commands;

namespace Axon.UI.ViewModels;

/// <summary>
/// The only screen at start-up when an earlier erase did not finish (see
/// <see cref="StartupErase.Decide"/>). Either it asks the person to erase now (typing
/// ERASE again) or to cancel the erase and open the app with the data, or it finishes the
/// erase it was told to finish and says so. <see cref="ContinueRequested"/> opens the app.
/// </summary>
public sealed class StartupEraseViewModel : INotifyPropertyChanged
{
    private readonly DataWipeService _wipe;
    private readonly StartupEraseAction _action;
    private readonly bool _hardwareVault;
    private readonly AsyncCommand _eraseNowCommand;
    private readonly AsyncCommand _retryCommand;
    private readonly DelegateCommand _cancelEraseCommand;
    private readonly DelegateCommand _continueCommand;

    internal StartupEraseViewModel(StartupEraseAction action, DataWipeService wipe, bool hardwareVault)
    {
        _action = action;
        _wipe = wipe;
        _hardwareVault = hardwareVault;
        _eraseNowCommand = new AsyncCommand(RunEraseAsync, () => IsAsking && IsConfirmed);
        _retryCommand = new AsyncCommand(RunEraseAsync, () => CanRetry);
        _cancelEraseCommand = new DelegateCommand(CancelErase, () => IsAsking);
        _continueCommand = new DelegateCommand(Continue, () => CanContinue);

        if (action == StartupEraseAction.AskAgain)
        {
            _isAsking = true;
            _message = AskMessage();
        }
        else
        {
            _message = "Finishing the erase you started earlier…";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the app can open: the erase finished, or the person cancelled it.</summary>
    public event EventHandler? ContinueRequested;

    private string _message;
    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    private bool _isAsking;
    /// <summary>The person chooses: erase now (after typing the word) or cancel the erase.</summary>
    public bool IsAsking
    {
        get => _isAsking;
        private set
        {
            if (SetField(ref _isAsking, value)) RaiseCommands();
        }
    }

    private bool _canRetry;
    /// <summary>An erase ran and left some of Axon's files; the app stays closed until it finishes.</summary>
    public bool CanRetry
    {
        get => _canRetry;
        private set
        {
            if (SetField(ref _canRetry, value)) RaiseCommands();
        }
    }

    private bool _canContinue;
    /// <summary>The erase finished; the person has been told and can open the (empty) app.</summary>
    public bool CanContinue
    {
        get => _canContinue;
        private set
        {
            if (SetField(ref _canContinue, value)) RaiseCommands();
        }
    }

    private string _confirmationText = "";
    public string ConfirmationText
    {
        get => _confirmationText;
        set
        {
            if (SetField(ref _confirmationText, value ?? "")) RaiseCommands();
        }
    }

    private bool IsConfirmed =>
        string.Equals(ConfirmationText, SettingsViewModel.EraseConfirmationWord, StringComparison.Ordinal);

    public ICommand EraseNowCommand => _eraseNowCommand;

    public ICommand RetryCommand => _retryCommand;

    public ICommand CancelEraseCommand => _cancelEraseCommand;

    public ICommand ContinueCommand => _continueCommand;

    /// <summary>Finishes the erase now when start-up was told to; otherwise waits for the person.</summary>
    public Task StartAsync() =>
        _action == StartupEraseAction.FinishErase ? RunEraseAsync() : Task.CompletedTask;

    private async Task RunEraseAsync()
    {
        IsAsking = false;
        CanRetry = false;
        Message = "Erasing…";

        var result = await Task.Run(_wipe.WipeAsync);
        switch (result.Outcome)
        {
            case DataWipeOutcome.Complete:
                Message = "The erase you started earlier has finished.\n\n" + result.Describe() +
                          "\n\nAxon now opens empty.";
                CanContinue = true;
                break;

            case DataWipeOutcome.NothingErased:
                // The database key survived, so the data is intact: the person chooses again.
                Message = result.Describe();
                ConfirmationText = "";
                IsAsking = true;
                break;

            default:
                Message = "Axon has not finished erasing your data, so it will not open.\n\n" + result.Describe();
                CanRetry = true;
                break;
        }
    }

    /// <summary>Opens the app once: a second click must not build a second runtime.</summary>
    private void Continue()
    {
        if (!CanContinue) return;
        CanContinue = false;
        ContinueRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelErase()
    {
        var error = _wipe.CancelPending();
        if (error is not null)
        {
            Message = $"The erase could not be cancelled ({error}).\n\n" + AskMessage();
            return;
        }

        IsAsking = false;
        ContinueRequested?.Invoke(this, EventArgs.Empty);
    }

    private string AskMessage() =>
        "An erase of Axon's data was started on this device and did not finish. " +
        (_hardwareVault
            ? "The database key is still here, so the erase did not get as far as your data. "
            : "This build uses the development key store, so Axon cannot tell how far the erase got: " +
              "some of your data may already be gone. ") +
        $"\n\nData folder: {_wipe.DataDirectory}\n\n" +
        "Type ERASE and choose Erase now to finish the erase, or cancel the erase to open Axon " +
        (_hardwareVault ? "with your data." : "with whatever is left.");

    private void RaiseCommands()
    {
        _eraseNowCommand.RaiseCanExecuteChanged();
        _retryCommand.RaiseCanExecuteChanged();
        _cancelEraseCommand.RaiseCanExecuteChanged();
        _continueCommand.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
