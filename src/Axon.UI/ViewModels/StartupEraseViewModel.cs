using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.UI.Application;
using Axon.UI.Commands;

namespace Axon.UI.ViewModels;

/// <summary>
/// The only screen at start-up when the vault cannot simply be opened (see
/// <see cref="StartupErase.Decide"/>):
///   • an erase did not finish and the data can still be opened: erase now (typing ERASE
///     again) or cancel the erase and open the app with the data;
///   • an erase did not finish and the database key no longer works: finish it, then say so;
///   • the database key no longer works although no erase is pending: erase what is left and
///     start fresh (typing ERASE), or close;
///   • the key store cannot be read: try again, or close. Nothing is erased.
/// <see cref="ContinueRequested"/> opens the app; <see cref="CloseRequested"/> quits.
/// </summary>
public sealed class StartupEraseViewModel : INotifyPropertyChanged
{
    private readonly DataWipeService _wipe;
    private readonly bool _hardwareVault;
    private readonly Func<StartupEraseAction> _recheck;
    private readonly AsyncCommand _eraseNowCommand;
    private readonly AsyncCommand _retryCommand;
    private readonly DelegateCommand _recheckCommand;
    private readonly DelegateCommand _cancelEraseCommand;
    private readonly DelegateCommand _continueCommand;
    private readonly DelegateCommand _closeCommand;
    private StartupEraseAction _action;

    /// <param name="recheck">Decides again from the current state of the disk (for "Try again").</param>
    internal StartupEraseViewModel(
        StartupEraseAction action, DataWipeService wipe, bool hardwareVault, Func<StartupEraseAction> recheck)
    {
        _wipe = wipe;
        _hardwareVault = hardwareVault;
        _recheck = recheck;
        _eraseNowCommand = new AsyncCommand(RunEraseAsync, () => IsAsking && IsConfirmed);
        _retryCommand = new AsyncCommand(RunEraseAsync, () => CanRetry);
        _recheckCommand = new DelegateCommand(Recheck, () => CanRecheck);
        _cancelEraseCommand = new DelegateCommand(CancelErase, () => CanCancelErase);
        _continueCommand = new DelegateCommand(Continue, () => CanContinue);
        _closeCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty), () => CanClose);
        _message = "";
        Show(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the app can open: the erase finished, or the person cancelled it.</summary>
    public event EventHandler? ContinueRequested;

    /// <summary>Raised when the person chooses to close Axon.</summary>
    public event EventHandler? CloseRequested;

    private string _message;
    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    private bool _isAsking;
    /// <summary>The typed word arms "Erase now" (an unfinished erase, or a fresh start).</summary>
    public bool IsAsking
    {
        get => _isAsking;
        private set => SetFlag(ref _isAsking, value);
    }

    private bool _canCancelErase;
    /// <summary>The data can still be opened, so cancelling the erase opens the app with it.</summary>
    public bool CanCancelErase
    {
        get => _canCancelErase;
        private set => SetFlag(ref _canCancelErase, value);
    }

    private bool _canRetry;
    /// <summary>An erase ran and left some of Axon's files; the app stays closed until it finishes.</summary>
    public bool CanRetry
    {
        get => _canRetry;
        private set => SetFlag(ref _canRetry, value);
    }

    private bool _canRecheck;
    /// <summary>The key store could not be read; looking again may succeed.</summary>
    public bool CanRecheck
    {
        get => _canRecheck;
        private set => SetFlag(ref _canRecheck, value);
    }

    private bool _canContinue;
    /// <summary>The erase finished; the person has been told and can open the (empty) app.</summary>
    public bool CanContinue
    {
        get => _canContinue;
        private set => SetFlag(ref _canContinue, value);
    }

    private bool _canClose;
    public bool CanClose
    {
        get => _canClose;
        private set => SetFlag(ref _canClose, value);
    }

    private string _confirmationText = "";
    public string ConfirmationText
    {
        get => _confirmationText;
        set => SetFlag(ref _confirmationText, value ?? "");
    }

    private bool IsConfirmed =>
        string.Equals(ConfirmationText, SettingsViewModel.EraseConfirmationWord, StringComparison.Ordinal);

    public ICommand EraseNowCommand => _eraseNowCommand;

    public ICommand RetryCommand => _retryCommand;

    public ICommand RecheckCommand => _recheckCommand;

    public ICommand CancelEraseCommand => _cancelEraseCommand;

    public ICommand ContinueCommand => _continueCommand;

    public ICommand CloseCommand => _closeCommand;

    /// <summary>Finishes the erase now when start-up was told to; otherwise waits for the person.</summary>
    public Task StartAsync() =>
        _action == StartupEraseAction.FinishErase ? RunEraseAsync() : Task.CompletedTask;

    private void Show(StartupEraseAction action)
    {
        _action = action;
        ClearChoices();
        switch (action)
        {
            case StartupEraseAction.AskAgain:
                Message = AskMessage();
                IsAsking = true;
                CanCancelErase = true;
                break;

            case StartupEraseAction.KeyUnusable:
                Message = "The database key on this device cannot be read, so the data stored here cannot be " +
                          $"opened.\n\nData folder: {_wipe.DataDirectory}\n\nType ERASE and choose Erase now to erase " +
                          "what is left and start fresh, or close Axon.";
                IsAsking = true;
                CanClose = true;
                break;

            case StartupEraseAction.KeyUnreadable:
                Message = "Axon cannot read its key store right now (another copy of Axon, or a backup or " +
                          "antivirus program, may be holding it), so it cannot tell whether your data can be " +
                          $"opened. Nothing has been erased.\n\nData folder: {_wipe.DataDirectory}\n\n" +
                          "Close whatever may be using it, then try again.";
                CanRecheck = true;
                CanClose = true;
                break;

            default:
                Message = "Finishing the erase you started earlier…";
                break;
        }
    }

    private async Task RunEraseAsync()
    {
        ClearChoices();
        Message = "Erasing…";

        var result = await Task.Run(_wipe.WipeAsync);
        switch (result.Outcome)
        {
            case DataWipeOutcome.Complete:
                Message = (_action == StartupEraseAction.KeyUnusable
                              ? "What was left has been erased.\n\n"
                              : "The erase you started earlier has finished.\n\n")
                          + result.Describe() + "\n\nAxon now opens empty.";
                CanContinue = true;
                break;

            case DataWipeOutcome.NothingErased:
                // The database key still unprotects, so nothing was deleted: choose again.
                Message = result.Describe();
                ConfirmationText = "";
                IsAsking = true;
                CanCancelErase = true;
                break;

            default:
                Message = "Axon has not finished erasing your data, so it will not open.\n\n" + result.Describe();
                CanRetry = true;
                CanClose = true;
                break;
        }
    }

    private void Recheck()
    {
        var action = _recheck();
        if (action == StartupEraseAction.OpenNormally)
        {
            ClearChoices();
            ContinueRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Show(action);
        if (action == StartupEraseAction.FinishErase)
            _ = RunEraseAsync();
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

        ClearChoices();
        ContinueRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClearChoices()
    {
        IsAsking = false;
        CanCancelErase = false;
        CanRetry = false;
        CanRecheck = false;
        CanContinue = false;
        CanClose = false;
    }

    private string AskMessage() =>
        "An erase of Axon's data was started on this device and did not finish. " +
        (_hardwareVault
            ? "The database key still works, and the erase deletes nothing before that key is gone, " +
              "so your data has not been erased. "
            : "This build uses the development key store, so Axon cannot tell how far the erase got: " +
              "some of your data may already be gone. ") +
        $"\n\nData folder: {_wipe.DataDirectory}\n\n" +
        "Type ERASE and choose Erase now to finish the erase, or cancel the erase to open Axon " +
        (_hardwareVault ? "with your data." : "with whatever is left.");

    private void SetFlag<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!SetField(ref field, value, name)) return;
        _eraseNowCommand.RaiseCanExecuteChanged();
        _retryCommand.RaiseCanExecuteChanged();
        _recheckCommand.RaiseCanExecuteChanged();
        _cancelEraseCommand.RaiseCanExecuteChanged();
        _continueCommand.RaiseCanExecuteChanged();
        _closeCommand.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
