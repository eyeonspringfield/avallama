// Copyright (c) Márk Csörgő and Martin Bartos
// Licensed under the MIT License. See LICENSE file for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using avallama.Constants.Application;
using avallama.ViewModels;
using avallama.Views;
using avallama.Views.Dialogs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;

namespace avallama.Services;

public enum ConfirmationType
{
    Positive,
    Negative
}

public class InputField(
    int maxLength = 255,
    string placeholder = "",
    string inputValue = "",
    bool isPassword = false,
    Func<string, bool>? validator = null,
    string validationErrorMessage = "")
{
    public int MaxLength { get; set; } = maxLength;
    public string Placeholder { get; set; } = placeholder;
    public string InputValue { get; set; } = inputValue;
    public bool IsPassword { get; set; } = isPassword;

    // we can provide when a value is valid in the validator, e.g. value => value.Contains("@") if we would check for email
    public Func<string, bool>? Validator { get; set; } = validator;

    // error message that will be shown if the validation fails
    public string ValidationErrorMessage { get; set; } = validationErrorMessage;

    public bool IsValid => Validator == null || Validator(InputValue);
}

public abstract record DialogResult;

public record ConfirmationResult(ConfirmationType Confirmation) : DialogResult;

public record InputResult(IEnumerable<string?> Results) : DialogResult;

public record NullResult(string ErrorMessage = "") : DialogResult;

public sealed record DialogRegistration(
    Func<Control> CreateView,
    Func<DialogViewModel> CreateViewModel);

public interface IDialogService
{
    void ShowDialog(
        ApplicationDialog dialog,
        bool resizable,
        double width,
        double height,
        double minWidth,
        double minHeight,
        double maxWidth,
        double maxHeight
    );

    void ShowInfoDialog(string informationMessage);

    void ShowErrorDialog(
        string errorMessage,
        bool shutDownApp
    );

    void ShowActionDialog(
        string title,
        string actionButtonText,
        Action action,
        Action? closeAction,
        string description,
        bool actionButtonOnly
    );

    Task<DialogResult> ShowConfirmationDialogAsync(
        string title,
        string description,
        string positiveButtonText,
        string negativeButtonText,
        ConfirmationType highlight
    );

    Task<DialogResult> ShowInputDialogAsync(
        string title,
        IEnumerable<InputField> fields,
        string description
    );

    void CloseDialog(ApplicationDialog dialog);
    void CloseDialog();
    void CloseAllDialogs();
}

public class DialogService(
    IReadOnlyDictionary<ApplicationDialog, DialogRegistration> registeredDialogs,
    IMessenger messenger)
    : IDialogService
{
    private Stack<DialogWindow> _dialogStack = new();

    /// <summary>
    /// Shows a custom, personalized dialog.
    /// For the specified <see cref="ApplicationDialog"/> type, it creates the dialog window based on the corresponding view.
    /// </summary>
    /// <param name="dialog">The type of dialog to show. Only unique types are allowed</param>
    /// <param name="resizable">The resizability of the dialog (optional)</param>
    /// <param name="width">The width of the dialog window (optional)</param>
    /// <param name="height">The height of the dialog window (optional)</param>
    /// <param name="minWidth">The minimum width of the dialog window (if it is resizable)</param>
    /// <param name="minHeight"> The minimum height of the dialog window (if it is resizable)</param>
    /// <param name="maxWidth">The maximum width of the dialog window (if it is resizable)</param>
    /// <param name="maxHeight"> The maximum height of the dialog window (if it is resizable)</param>
    /// <exception cref="InvalidOperationException">
    /// If the dialog type is not appropriate (e.g., Information, Error, Confirmation, Input), an exception is thrown.
    /// </exception>
    public void ShowDialog(
        ApplicationDialog dialog,
        bool resizable = false,
        double width = double.NaN,
        double height = double.NaN,
        double minWidth = double.NaN,
        double minHeight = double.NaN,
        double maxWidth = double.NaN,
        double maxHeight = double.NaN
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (dialog is ApplicationDialog.Information
                or ApplicationDialog.Error
                or ApplicationDialog.Confirmation
                or ApplicationDialog.Input
                or ApplicationDialog.Action)
            {
                throw new InvalidOperationException($"{dialog} dialog can not be used with ShowDialog.");
            }

            if (!registeredDialogs.TryGetValue(dialog, out var registration))
            {
                throw new NotSupportedException($"{dialog} dialog is not registered.");
            }

            var dialogWindow = new DialogWindow();
            var control = registration.CreateView();

            control.DataContext = registration.CreateViewModel();
            dialogWindow.DataContext = new DialogViewModel { DialogType = dialog };
            dialogWindow.Content = control;
            dialogWindow.CanResize = resizable;

            // if we provide a height or width value, it will set the resizing accordingly
            // so if we only provide width, it will adjust the height to the content and vice versa
            if (double.IsNaN(width) && !double.IsNaN(height))
            {
                dialogWindow.SizeToContent = SizeToContent.Width;
                dialogWindow.Height = height;
            }
            else if (!double.IsNaN(width) && double.IsNaN(height))
            {
                dialogWindow.SizeToContent = SizeToContent.Height;
                dialogWindow.Width = width;
            }
            else if (!double.IsNaN(height) && !double.IsNaN(width))
            {
                dialogWindow.SizeToContent = SizeToContent.Manual;
                dialogWindow.Height = height;
                dialogWindow.Width = width;
            }

            if (resizable)
            {
                if (!double.IsNaN(minWidth)) dialogWindow.MinWidth = minWidth;
                if (!double.IsNaN(minHeight)) dialogWindow.MinHeight = minHeight;
                if (!double.IsNaN(maxWidth)) dialogWindow.MaxWidth = maxWidth;
                if (!double.IsNaN(maxHeight)) dialogWindow.MaxHeight = maxHeight;
            }

            dialogWindow.InvalidateMeasure();
            ShowDialogWindow(dialogWindow);
        });
    }

    /// <summary>
    /// Shows an informational dialog, which only contains a text message.
    /// </summary>
    /// <param name="informationMessage">The informational message to be shown.</param>
    public void ShowInfoDialog(string informationMessage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var dialogWindow = new DialogWindow();
            var control = new InformationView
            {
                DialogMessage =
                {
                    Text = informationMessage.Replace(@"\n", Environment.NewLine)
                }
            };
            dialogWindow.Content = control;
            dialogWindow.DataContext = new DialogViewModel
            {
                DialogType = ApplicationDialog.Information
            };
            ShowDialogWindow(dialogWindow);
        });
    }

    /// <summary>
    /// Show a dialog containing an error message, which alerts the user to some problem.
    /// </summary>
    /// <param name="errorMessage">The error message to be shown.</param>
    /// <param name="shutdownApp">Shut down the application after closing the dialog (optional).</param>
    public void ShowErrorDialog(
        string errorMessage,
        bool shutdownApp = false
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            var dialogWindow = new DialogWindow();
            var control = new ErrorView
            {
                DialogMessage =
                {
                    Text = errorMessage.Replace(@"\n", Environment.NewLine)
                }
            };
            control.CloseButton.Click += (_, _) =>
            {
                CloseDialog(ApplicationDialog.Error);
                if (shutdownApp)
                {
                    messenger.Send(new ApplicationMessage.Shutdown());
                }
            };

            dialogWindow.Content = control;
            dialogWindow.DataContext = new DialogViewModel
            {
                DialogType = ApplicationDialog.Error
            };
            ShowDialogWindow(dialogWindow);
        });
    }

    /// <summary>
    /// Shows an action dialog with a customizable <see cref="Action"/> method that is executed when the
    /// action (left) button in the dialog is clicked.
    /// </summary>
    /// <param name="title">
    /// The title or question text of the dialog.
    /// </param>
    /// <param name="actionButtonText">
    /// The text of the left button.
    /// </param>
    /// <param name="action">
    /// The method that is invoked when the action button is clicked.
    /// </param>
    /// <param name="closeAction">
    /// The method that is invoked when the close button is clicked (optional).
    /// </param>
    /// <param name="description">
    /// The description of the dialog (optional).
    /// </param>
    /// <param name="actionButtonOnly">
    /// Only show the action button, without the close button (optional).
    /// </param>
    /// <example>
    /// Example usage:
    /// <code>
    /// _dialogService.ShowActionDialog(
    ///     title: LocalizationService.GetString("OLLAMA_NOT_INSTALLED"),
    ///     actionButtonText:  LocalizationService.GetString("DOWNLOAD"),
    ///     action: RedirectToOllamaDownload,
    ///     closeAction: _appService.Shutdown,
    ///     description: LocalizationService.GetString("OLLAMA_NOT_INSTALLED_DESC")
    /// );
    /// </code>
    /// </example>
    public void ShowActionDialog(
        string title,
        string actionButtonText,
        Action action,
        Action? closeAction = null,
        string description = "",
        bool actionButtonOnly = false
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            var dialogWindow = new DialogWindow();
            // ConfirmationView on purpose, as its View is reusable
            var control = new ConfirmationView
            {
                DialogTitle =
                {
                    Text = title
                }
            };

            if (!string.IsNullOrEmpty(description))
            {
                control.DialogDescription.Text = description;
            }
            else
            {
                control.DialogDescription.IsVisible = false;
            }

            control.PositiveButton.Content = actionButtonText;
            if (actionButtonOnly)
            {
                control.NegativeButton.IsVisible = false;
            }
            else
            {
                control.NegativeButton.Content = LocalizationService.GetString("CLOSE");
                control.NegativeButton.Classes.Add("secondaryButton");
                control.NegativeButton.Click += (_, _) =>
                {
                    CloseDialog(ApplicationDialog.Action);
                    closeAction?.Invoke();
                };
            }

            control.PositiveButton.Click += (_, _) =>
            {
                CloseDialog(ApplicationDialog.Action);
                action();
            };

            dialogWindow.Content = control;
            dialogWindow.DataContext = new DialogViewModel
            {
                DialogType = ApplicationDialog.Action
            };

            ShowDialogWindow(dialogWindow);
        });
    }
    // these methods have to be async because they have results that need to be awaited properly
    // if they were not async, they would try to await the result on the UI thread, causing it to freeze and preventing the result from being set with TrySetResult as the UI thread would already be busy

    /// <summary>
    /// Shows a confirmation dialog to the user with two selectable buttons.
    /// The method asynchronously waits for the user to click one of the buttons,
    /// then returns the selected result in a <see cref="DialogResult"/> type.
    /// </summary>
    /// <param name="title">
    /// The title or question text of the dialog.
    /// </param>
    /// <param name="positiveButtonText">
    /// The text of the left button.
    /// </param>
    /// <param name="negativeButtonText">
    /// The text of the right button.
    /// </param>
    /// <param name="description">
    /// The description of the dialog (optional).
    /// </param>
    /// <param name="highlight">
    /// The type of button to be highlighted, making it visually more prominent.
    /// </param>
    /// <returns>
    /// A <see cref="Task{DialogResult}"/>, which contains the user's choice.
    /// The return value can be a <see cref="ConfirmationResult"/>, which contains the selected <see cref="ConfirmationType"/> value.
    /// </returns>
    /// <example>
    /// Example usage:
    /// <code>
    /// var result = await _dialogService.ShowConfirmationDialog("Are you sure you want to delete ...?", "Delete", "Cancel", ConfirmationType.Negative);
    /// if (result is ConfirmationResult confRes)
    /// {
    ///     if (confRes.Confirmation == ConfirmationType.Positive)
    ///     {
    ///         // the user clicked the positive (delete) button
    ///     }
    /// }
    /// </code>
    /// </example>
    public async Task<DialogResult> ShowConfirmationDialogAsync(
        string title,
        string positiveButtonText,
        string negativeButtonText,
        string description = "",
        ConfirmationType highlight = ConfirmationType.Positive
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialogResult = new TaskCompletionSource<DialogResult>();
            var dialogWindow = new DialogWindow();
            var control = new ConfirmationView
            {
                DialogTitle =
                {
                    Text = title
                }
            };

            if (!string.IsNullOrEmpty(description))
            {
                control.DialogDescription.Text = description;
            }
            else
            {
                control.DialogDescription.IsVisible = false;
            }

            control.PositiveButton.Content = positiveButtonText;
            control.NegativeButton.Content = negativeButtonText;

            // if we want to highlight the positive button, then we give the negative button a less prominent appearance, and vice versa
            // this class is already defined in styles
            if (highlight == ConfirmationType.Positive)
            {
                control.NegativeButton.Classes.Add("secondaryButton");
            }
            else if (highlight == ConfirmationType.Negative)
            {
                control.PositiveButton.Classes.Add("secondaryButton");
            }

            control.PositiveButton.Click += (_, _) =>
            {
                dialogResult.TrySetResult(new ConfirmationResult(ConfirmationType.Positive));
                CloseDialog(ApplicationDialog.Confirmation);
            };

            control.NegativeButton.Click += (_, _) =>
            {
                dialogResult.TrySetResult(new ConfirmationResult(ConfirmationType.Negative));
                CloseDialog(ApplicationDialog.Confirmation);
            };

            dialogWindow.Content = control;
            dialogWindow.DataContext = new DialogViewModel
            {
                DialogType = ApplicationDialog.Confirmation
            };

            // if a user closes the dialog in some other way, we handle the task
            dialogWindow.Closing += (_, _) =>
            {
                if (!dialogResult.Task.IsCompleted)
                {
                    dialogResult.TrySetResult(new NullResult("DialogWindow closed before returning result"));
                }
            };
            await ShowDialogWindowAsync(dialogWindow);
            var result = await dialogResult.Task;
            return result;
        });
    }

    /// <summary>
    /// Shows a dialog containing TextBox field(s) that allow the user to input multiple text values as <see cref="InputField"/> types.
    /// The dialog contains two buttons: save and close. Based on the user's response, it returns a <see cref="DialogResult"/>.
    /// </summary>
    /// <param name="title">The title of the dialog</param>
    /// <param name="description">Additional description for the dialog (optional).</param>
    /// <param name="inputFields">Input fields</param>
    /// <returns>
    /// A <see cref="Task{DialogResult}"/> which contains the text provided by the user in the form of <see cref="InputResult"/>,
    /// or <see cref="NullResult"/> if the dialog was closed without a response.
    /// </returns>
    /// <example>
    /// Example usage:
    /// <code>
    /// var dialogResult = await _dialogService.ShowInputDialog(
    ///     title: LocalizationService.GetString("OLLAMA_REMOTE_DIALOG_TITLE"),
    ///     description: LocalizationService.GetString("OLLAMA_REMOTE_DIALOG_DESC"),
    ///     inputFields: new List()
    ///     {
    ///         new (placeholder: LocalizationService.GetString("API_HOST_SETTING")),
    ///         new (
    ///             placeholder: LocalizationService.GetString("API_PORT_SETTING"),
    ///             inputValue: 11434.ToString()
    ///         )
    ///     }
    /// );
    ///
    /// if (dialogResult is InputResult inpResult)
    /// {
    ///     var count = 0;
    ///     foreach (var result in inpResult.Results)
    ///     {
    ///         Console.WriteLine($"({count}) Input Field Value: {result}");
    ///         count++;
    ///     }
    /// }
    /// </code>
    /// </example>
    public async Task<DialogResult> ShowInputDialogAsync(
        string title,
        IEnumerable<InputField> inputFields,
        string description = ""
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var fields = inputFields.ToList();
            if (fields.Count == 0) return new NullResult("Empty input fields");

            var dialogResult = new TaskCompletionSource<DialogResult>();
            var dialogWindow = new DialogWindow();
            var control = new InputView
            {
                DialogTitle =
                {
                    Text = title
                }
            };

            if (!string.IsNullOrEmpty(description))
            {
                control.DialogDescription.Text = description;
            }
            else
            {
                control.DialogDescription.IsVisible = false;
            }

            control.InputFieldsStackPanel.Children.Clear();
            control.ErrorMessage.IsVisible = false;

            foreach (var field in fields)
            {
                var inputTextBox = new TextBox();
                inputTextBox.Classes.Add("settingTextBox");

                if (field.IsPassword)
                {
                    inputTextBox.PasswordChar = '*';
                }

                if (!string.IsNullOrEmpty(field.Placeholder))
                    inputTextBox.Watermark = field.Placeholder;

                if (!string.IsNullOrEmpty(field.InputValue))
                    inputTextBox.Text = field.InputValue;

                inputTextBox.MaxLength = field.MaxLength;
                control.InputFieldsStackPanel.Children.Add(inputTextBox);
            }

            control.CloseButton.Click += (_, _) =>
            {
                // when closing the dialog without saving it does not return a result, as the user did not want to save anything
                dialogResult.TrySetResult(new NullResult());
                CloseDialog(ApplicationDialog.Input);
            };

            control.SaveButton.Click += (_, _) =>
            {
                control.ErrorMessage.IsVisible = false;
                control.ErrorMessage.Text = string.Empty;

                // validation check
                for (var i = 0; i < fields.Count; i++)
                {
                    if (control.InputFieldsStackPanel.Children[i] is TextBox fieldTextBox)
                    {
                        // we update the content of the input fields, so we can revalidate them
                        fields[i].InputValue = fieldTextBox.Text ?? string.Empty;
                    }

                    if (!fields[i].IsValid)
                    {
                        control.ErrorMessage.IsVisible = true;
                        control.ErrorMessage.Text = fields[i].ValidationErrorMessage;
                        return;
                    }
                }

                // the textboxes, or the input fields' texts are combined into a List<string>
                var inputList = control.InputFieldsStackPanel.Children.Select(item =>
                        item as TextBox
                    )
                    .OfType<TextBox>()
                    .Select(textBoxItem => textBoxItem.Text)
                    .ToList();

                dialogResult.TrySetResult(new InputResult(inputList));
                CloseDialog(ApplicationDialog.Input);
            };

            dialogWindow.Content = control;
            dialogWindow.DataContext = new DialogViewModel
            {
                DialogType = ApplicationDialog.Input
            };

            // if the user closes the dialog in some other way, we handle the task
            dialogWindow.Closing += (_, _) =>
            {
                if (!dialogResult.Task.IsCompleted)
                {
                    dialogResult.TrySetResult(new NullResult("DialogWindow closed before returning result"));
                }
            };
            await ShowDialogWindowAsync(dialogWindow);
            var result = await dialogResult.Task;
            return result;
        });
    }

    /// <summary>
    /// Displays a dialog that is pushed onto the top of the dialog stack.
    /// If there is no open dialog, it attaches to the MainWindow.
    /// </summary>
    /// <param name="dialogWindow">The dialog window to be displayed.</param>
    private void ShowDialogWindow(DialogWindow dialogWindow)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var parent = _dialogStack.Count > 0
                ? _dialogStack.Peek()
                : Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;
            if (parent == null)
            {
                dialogWindow.Show();
            }
            else
            {
                dialogWindow.ShowDialog(parent);
            }

            _dialogStack.Push(dialogWindow);
            dialogWindow.Closing += (_, _) =>
            {
                if (_dialogStack.Peek() == dialogWindow)
                    _dialogStack.Pop();
                else
                    _dialogStack = new Stack<DialogWindow>(_dialogStack.Where(w => w != dialogWindow));
            };
        });
    }

    /// <summary>
    /// Asynchronously displays a dialog that is pushed onto the top of the dialog stack.
    /// If there is no open dialog, it attaches to the MainWindow. The method waits until the dialog is closed.
    /// </summary>
    /// <param name="dialogWindow">The dialog to be displayed.</param>
    /// <returns>
    /// A <see cref="Task"/> that completes when the dialog is closed.
    /// </returns>
    private async Task ShowDialogWindowAsync(DialogWindow dialogWindow)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var parent = _dialogStack.Count > 0
                ? _dialogStack.Peek()
                : Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;

            _dialogStack.Push(dialogWindow);
            dialogWindow.Closing += (_, _) =>
            {
                if (_dialogStack.Peek() == dialogWindow)
                    _dialogStack.Pop();
                else
                    _dialogStack = new Stack<DialogWindow>(_dialogStack.Where(w => w != dialogWindow));
            };
            // it's important that the dialog display with await is the last thing, because with await it will wait until the dialog is closed
            // and if we do not set up the dialog before (adding to stack, adding closing operations) then it cannot be closed
            if (parent == null)
            {
                dialogWindow.Show();
            }
            else
            {
                await dialogWindow.ShowDialog(parent);
            }
        });
    }

    /// <summary>
    /// Closes the dialog of the specified type, if found in the stack.
    /// </summary>
    /// <param name="dialog">The type of dialog to close.</param>
    public void CloseDialog(ApplicationDialog dialog)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var dialogWindow = _dialogStack.FirstOrDefault(d => d.DataContext is DialogViewModel viewModel
                                                                && viewModel.DialogType == dialog);
            if (dialogWindow == null) return;
            dialogWindow.Close();
            _dialogStack = new Stack<DialogWindow>(_dialogStack.Where(w => w != dialogWindow));
        });
    }

    /// <summary>
    /// Closes the most recently opened dialog from the top of the stack.
    /// </summary>
    public void CloseDialog()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_dialogStack.Count <= 0) return;
            var dialogWindow = _dialogStack.Pop();
            dialogWindow.Close();
        });
    }

    /// <summary>
    /// Closes all open dialogs and clears the dialog stack.
    /// </summary>
    public void CloseAllDialogs()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var dialogWindow in _dialogStack.ToList())
            {
                dialogWindow.Close();
            }

            _dialogStack.Clear();
        });
    }
}
