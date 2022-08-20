﻿using System.Diagnostics;

using Ookii.Dialogs.Wpf;

using Scover.WinClean.BusinessLogic;
using Scover.WinClean.BusinessLogic.Scripts;
using Scover.WinClean.DataAccess;
using Scover.WinClean.Presentation.Dialogs;
using Scover.WinClean.Resources;
using Scover.WinClean.Resources.UI;

namespace Scover.WinClean.Presentation.ScriptExecution;

/// <summary>
/// Walks the user through the multi-step high-level operation of executing multiple scripts asynchronously by displaying a task
/// dialog tracking the progress.
/// </summary>
public class ScriptExecutionWizard
{
    private static readonly ScriptExecutor executor = new();
    private readonly IReadOnlyList<Script> _scripts;

    /// <param name="scripts">The scripts to execute.</param>
    /// <exception cref="ArgumentException"><paramref name="scripts"/> is empty.</exception>
    public ScriptExecutionWizard(IEnumerable<Script> scripts)
    {
        _scripts = scripts.ToList();
        if (!_scripts.Any())
        {
            throw new ArgumentException(DevException.CollectionEmpty, nameof(scripts));
        }
        executor.ProgressChanged += (_, e) => Logs.ScriptExecuting.FormatWith(_scripts[e.ScriptIndex]);
    }

    /// <summary>Executes the script(s) and displays a dialog tracking the progress.</summary>
    public void Execute()
    {
        Happenings.ScriptExecution.SetAsHappening();

        CommandLink yes = new()
        {
            Text = SystemRestorePoint.CommandLinkYes,
            Note = SystemRestorePoint.CommandLinkYesNote
        };

        using CommandLinkDialog restorePointDialog = new()
        {
            MainInstruction = SystemRestorePoint.MainInstruction,
            CustomMainIcon = Helpers.GetRestorePointIcon(),
            CommandLinks =
            {
                yes,
                new()
                {
                    Text = SystemRestorePoint.CommandLinkNo
                }
            },
            DefaultCommandLink = yes
        };
        DialogResult result = restorePointDialog.ShowDialog();

        if (result.WasClosed || result.ClickedButton == Button.Cancel)
        {
            return;
        }

        if (ReferenceEquals(result.ClickedCommandLink, yes))
        {
            try
            {
                CreateRestorePoint();
            }
            catch (InvalidOperationException e)
            {
                Logs.RestorePointCreationError.FormatWith(e.Message).Log(LogLevel.Error);

                bool? enableSystemRestore = ShowEnableSystemRestoreDialog();

                if (enableSystemRestore is null)
                {
                    return;
                }

                if (enableSystemRestore.Value)
                {
                    RestorePoint.EnableSystemRestore();
                    Logs.SystemRestoreEnabled.Log(LogLevel.Info);
                    CreateRestorePoint();
                }
            }
        }

        (bool completed, int elapsedSeconds, bool autoRestart) = ShowProgressDialog();
        if (completed)
        {
            ShowCompletedDialog(elapsedSeconds, autoRestart);
        }
    }

    private static void CreateRestorePoint()
    {
        Logs.CreatingRestorePoint.Log(LogLevel.Info);
        new RestorePoint(AppInfo.Name,
            EventType.BeginSystemChange,
            RestorePointType.ModifySettings).Create();
        Logs.RestorePointCreated.Log(LogLevel.Info);
    }

    private static void RebootForApplicationMaintenance()
    {
        Logs.RebootingForAppMaintenance.Log(LogLevel.Info);
        _ = Process.Start("shutdown", "/g /t 0 /d p:4:1");
    }

    private static bool? ShowEnableSystemRestoreDialog()
    {
        CommandLink enable = new()
        {
            Text = SystemProtectionDisabled.CommandLinkEnable,
            Note = SystemProtectionDisabled.CommandLinkEnableNote
        };

        using CommandLinkDialog enableSystemRestore = new()
        {
            MainInstruction = SystemProtectionDisabled.MainInstruction,
            MainIcon = TaskDialogIcon.Error,
            CommandLinks =
            {
                enable,
                new()
                {
                    Text = SystemProtectionDisabled.CommandLinkContinueAnyway,
                    Note = SystemProtectionDisabled.CommandLinkContinueAnywayNote
                }
            }
        };
        DialogResult result = enableSystemRestore.ShowDialog();

        // true if user chose to enable system restore; false if user chose to continue anyway; null if the user canceled or
        // closed the dialog.
        return result.WasClosed || result.ClickedButton == Button.Cancel ? null : ReferenceEquals(result.ClickedCommandLink, enable);
    }

    private static bool ShowHungScriptDialog(string scriptName)
    {
        using TimeoutDialog hungScriptDialog = new(Button.EndTask, Button.Ignore)
        {
            MainIcon = TaskDialogIcon.Warning,
            Content = Resources.UI.Dialogs.HungScriptDialogContent.FormatWith(scriptName, AppInfo.Settings.ScriptTimeout),
            Timeout = TimeSpan.FromSeconds(10),
            TimeoutButton = Button.Ignore
        };
        return hungScriptDialog.ShowDialog().ClickedButton != Button.EndTask;
    }

    private async Task ExecuteScriptsAsync()
    {
        Logs.StartingExecutionOfScripts.FormatWith(_scripts.Count).Log(LogLevel.Info);

        await executor.ExecuteScriptsAsync(_scripts, ShowHungScriptDialog).ConfigureAwait(false);

        Logs.ScriptsExecuted.Log(LogLevel.Info);
    }

    private void ShowCompletedDialog(int elapsedSeconds, bool autoRestart)
    {
        using Dialog completedDialog = new(Button.Restart, Button.OK)
        {
            ExpandedInformation = CompletedDialog.ExpandedInformation.FormatWith(_scripts.Count, TimeSpan.FromSeconds(elapsedSeconds)),
            StartExpanded = AppInfo.Settings.DetailsAfterExecution,
            MainInstruction = CompletedDialog.MainInstruction,
            Content = CompletedDialog.Content,
        };
        completedDialog.ExpandButtonClicked += (_, _) => AppInfo.Settings.DetailsAfterExecution ^= true;

        if (autoRestart || completedDialog.ShowDialog().ClickedButton == Button.Restart)
        {
            RebootForApplicationMaintenance();
        }
    }

    private (bool completed, int elapsedSeconds, bool autoRestart) ShowProgressDialog()
    {
        using ProgressDialog progress = new(_scripts);

        executor.ProgressChanged += (_, e) => progress.ScriptIndex = e.ScriptIndex;

        progress.Created += async (_, _) =>
        {
            await ExecuteScriptsAsync().ConfigureAwait(true);
            progress.Close();
        };
        bool completed = progress.Show().ClickedButton != Button.Stop;
        if (!completed)
        {
            executor.CancelScriptExecution();
            Logs.ScriptExecutionCanceled.Log(LogLevel.Info);
        }
        return (completed, progress.ElapsedSeconds, progress.AutoRestart);
    }
}