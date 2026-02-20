using System.Collections.Specialized;
using NekoSharp.App.ViewModels;

namespace NekoSharp.App.Views;

public class LogWindow : Adw.Window
{
    private readonly MainWindowViewModel _vm;
    private readonly Gtk.Box _logEntriesBox;
    private readonly Gtk.ScrolledWindow _scrolledWindow;

    public LogWindow(MainWindowViewModel viewModel, Adw.Application app)
    {
        _vm = viewModel;
        
        SetApplication(app);  
        SetTitle("Logs");
        SetDefaultSize(600, 400);
        SetModal(false);

         
        this.OnCloseRequest += (s, e) => {
            this.SetVisible(false);
            return true;
        };

         
        var headerBar = Adw.HeaderBar.New();
        var contentBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        contentBox.Append(headerBar);

         
        _scrolledWindow = Gtk.ScrolledWindow.New();
        _scrolledWindow.SetVexpand(true);
        _scrolledWindow.SetHexpand(true);
        _scrolledWindow.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);

        _logEntriesBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        _logEntriesBox.AddCssClass("log-box");
        _logEntriesBox.SetMarginTop(10);
        _logEntriesBox.SetMarginBottom(10);
        _logEntriesBox.SetMarginStart(10);
        _logEntriesBox.SetMarginEnd(10);
        _logEntriesBox.SetSpacing(4);

        _scrolledWindow.SetChild(_logEntriesBox);
        contentBox.Append(_scrolledWindow);

         
        var clearBtn = Gtk.Button.New();
        clearBtn.SetIconName("edit-clear-all-symbolic");
        clearBtn.SetTooltipText("Clear Logs");
        clearBtn.OnClicked += (s, e) => _vm.LogEntries.Clear();
        headerBar.PackEnd(clearBtn);

        SetContent(contentBox);

         
        RebuildLogEntries();
        _vm.LogEntries.CollectionChanged += OnLogEntriesCollectionChanged;
    }

    private void OnLogEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        GLib.Functions.IdleAdd(0, () =>
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (e.NewItems != null)
                    {
                        foreach (LogEntryViewModel logVm in e.NewItems)
                            AddLogEntryWidget(logVm);
                    }
                    break;

                case NotifyCollectionChangedAction.Reset:
                    ClearLogEntryWidgets();
                    break;

                 
                default:
                    RebuildLogEntries();
                    break;
            }
            return false;
        });
    }

    private void AddLogEntryWidget(LogEntryViewModel logVm)
    {
        var entryBox = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        entryBox.AddCssClass("log-entry");
        entryBox.SetMarginBottom(8);

         
        var headerRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        
        var timestamp = Gtk.Label.New(logVm.Timestamp);
        timestamp.AddCssClass("dim-label");
        headerRow.Append(timestamp);

        var levelLabel = Gtk.Label.New(logVm.Level.ToUpper());
        levelLabel.AddCssClass("caption-heading");
        
         
        switch (logVm.Entry.Level)
        {
            case Core.Services.LogLevel.Debug:
                levelLabel.AddCssClass("dim-label");
                break;
            case Core.Services.LogLevel.Info:
                levelLabel.AddCssClass("accent-text");
                break;
            case Core.Services.LogLevel.Warning:
                levelLabel.AddCssClass("warning-text");
                break;
            case Core.Services.LogLevel.Error:
                levelLabel.AddCssClass("error-text");
                break;
        }
        headerRow.Append(levelLabel);
        entryBox.Append(headerRow);

         
        var message = Gtk.Label.New(logVm.Message);
        message.SetHalign(Gtk.Align.Start);
        message.SetWrap(true);
        message.SetSelectable(true);
        message.SetXalign(0);
        entryBox.Append(message);

         
        if (logVm.HasDetails)
        {
            var detailsExpander = Gtk.Expander.New("Details");
            var detailsLabel = Gtk.Label.New(logVm.Details);
            detailsLabel.SetWrap(true);
            detailsLabel.SetSelectable(true);
            detailsLabel.SetXalign(0);
            detailsLabel.AddCssClass("monospace");
            detailsExpander.SetChild(detailsLabel);
            entryBox.Append(detailsExpander);
        }

         
        entryBox.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));

        _logEntriesBox.Append(entryBox);
        ScrollToBottom();
    }

    private void ClearLogEntryWidgets()
    {
        var child = _logEntriesBox.GetFirstChild();
        while (child != null)
        {
            _logEntriesBox.Remove(child);
            child = _logEntriesBox.GetFirstChild();
        }
    }

    private void RebuildLogEntries()
    {
        ClearLogEntryWidgets();
        foreach (var logVm in _vm.LogEntries)
        {
            AddLogEntryWidget(logVm);
        }
    }

    private void ScrollToBottom()
    {
         
        var vadj = _scrolledWindow.GetVadjustment();
        vadj.SetValue(vadj.GetUpper() - vadj.GetPageSize());
    }
}
