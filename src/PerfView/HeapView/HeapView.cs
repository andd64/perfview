using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Documents;
using System.Windows.Data;
using System.ComponentModel;
using System.Globalization;

using Microsoft.Diagnostics.Tracing;
using Address = System.UInt64;
using Stats;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace PerfView
{
    /// <summary>
    /// Managed Memory Analyzer main window
    /// </summary>
    public class MemoryAnalyzer : PerfViewTreeItem
    {
        const string ShortTitle = "GC Heap Analyzer";
        const string FullTitle  = "Microsoft Managed Memory Analyzer ({0}, pid={1})";

        PerfViewFile m_dataFile;
        StatusBar    m_worker;
        TraceLog     m_traceLog;

        ProcessMemoryInfo m_heapInfo;
        
        Window    m_mainWindow;

        GridRow   m_gcInfoPanel;
        GridRow   m_heapAllocPanel;
        GridRow   m_threadPanel;
        GridRow   m_issuePanel;
        GridRow   m_heapDiagramPanel;

        StatusBar m_statusBar;

        TextBox   m_helpBox;
                
        public MemoryAnalyzer(PerfViewFile dataFile)
        {
            m_dataFile = dataFile;
            Name       = ShortTitle;
        }

        public override string HelpAnchor 
        { 
            get 
            { 
                return Name.Replace(" ", ""); 
            } 
        }
        
        public override string FilePath 
        { 
            get 
            { 
                return m_dataFile.FilePath; 
            } 
        }

        void OnSaveAsXPS(object sender, RoutedEventArgs e)
        {
            m_heapInfo.SaveDiagram(FilePath, true);
        }

        void OnSaveAsPNG(object sender, RoutedEventArgs e)
        {
            m_heapInfo.SaveDiagram(FilePath, false);
        }

        void OnCloseWindow(object sender, RoutedEventArgs e)
        {
            if (m_mainWindow != null)
            {
                m_mainWindow.Close();
            }
        }

        const string GCEventPanelName      = "GC Event Panel";
        const string ThreadPanelName       = "Thread Panel";
        const string AllocTickPanelName    = "Allocation Tick Event Panel";
        const string HeapDiagramPanelName  = "Heap Diagram Panel";
        const string IssuePanelName        = "Issue Panel";

        Menu CreateMainMenu()
        {
            Menu menu = new Menu();
            menu.IsMainMenu = true;
            menu.Height = 20;

            MenuItem file = menu.AddItem("File");
            file.AddItem("Open").IsEnabled = false;
            file.AddItem("Save as XPS ...").Click += OnSaveAsXPS;
            file.AddItem("Save as PNG ...").Click += OnSaveAsPNG;
            file.AddItem("Close").Click += OnCloseWindow;

            MenuItem view = menu.AddItem("View");
            
               m_gcInfoPanel.HookupVisibility(view.AddItem(GCEventPanelName, true, true));
            m_heapAllocPanel.HookupVisibility(view.AddItem(AllocTickPanelName, true, false));
               m_threadPanel.HookupVisibility(view.AddItem(ThreadPanelName, true, false));
                m_issuePanel.HookupVisibility(view.AddItem(IssuePanelName, true, false));
             
            return menu;
        }

        void LaunchViewer(List<IProcess> selectedProcesses)
        {
            // Single process only
            if (selectedProcesses == null && (selectedProcesses.Count != 1))
            {
                return;
            }

            IProcess proc = selectedProcesses[0];

            int procID = proc.ProcessID;

            // Create the application's main window
            m_mainWindow = new Window();
            m_mainWindow.Title = String.Format(FullTitle, proc.Name, procID);

            // HelpBox
            m_helpBox = new TextBox();
            m_helpBox.TextWrapping = TextWrapping.Wrap;
            m_helpBox.Foreground   = Brushes.Blue;

            m_statusBar = new StatusBar();
            m_statusBar.Height = 24;
                        
            // GCInfoView Panel
            GcInfoView gcinfo = new GcInfoView();

            // Grid for 5 panels with adjustable heights
            Grid grid = new Grid();
            
            m_gcInfoPanel = new GridRow(grid, gcinfo.CreateGCInfoPanel(m_helpBox).Wrap(GCEventPanelName), true, true, 0, 111);
           
            // HeapTickView Panel
            HeapAllocView heapAlloc = new HeapAllocView();
            m_heapAllocPanel = new GridRow(grid, heapAlloc.CreateHeapAllocPanel(m_traceLog).Wrap(AllocTickPanelName), false, true, 1, 111);

            // Thread Panel
            ThreadView threadView = new ThreadView();
            m_threadPanel = new GridRow(grid, threadView.CreateThreadViewPanel().Wrap(ThreadPanelName), false, true, 2, 111);

            // Issue Panel
            IssueView issue = new IssueView();
            m_issuePanel = new GridRow(grid, issue.CreateIssuePanel(m_helpBox).Wrap(IssuePanelName), false, true, 3, 111);

            // HeapDiagram Panel
            HeapDiagram diagram = new HeapDiagram(m_dataFile, m_statusBar, m_mainWindow);
            m_heapDiagramPanel = new GridRow(grid, diagram.CreateHeapDiagramPanel(m_helpBox).Wrap(HeapDiagramPanelName), true, false, 4, 111);
                        
            DockPanel main = new DockPanel();

            main.DockBottom(m_statusBar);

            main.DockTop(CreateMainMenu());
            main.DockBottom(grid);
            
            m_mainWindow.Content = main;
            m_mainWindow.Closed += CloseMainWindow;
            m_mainWindow.Show();

            // Load events for the process
            m_heapInfo = new ProcessMemoryInfo(m_traceLog, m_dataFile, m_statusBar);

            m_statusBar.StartWork(String.Format("Loading events for process {0} (pid={1})", proc.Name, procID), delegate()
            {
                m_heapInfo.LoadEvents(procID, (int) m_traceLog.SampleProfileInterval.Ticks);
             
                m_statusBar.EndWork(delegate()
                {   
                    gcinfo.SetGCEvents(m_heapInfo.GcEvents);
                    heapAlloc.SetAllocEvents(m_heapInfo.m_allocSites);
                    threadView.SetData(m_heapInfo);
                    diagram.SetData(m_heapInfo);
                    issue.SetData(m_heapInfo);
                });
            });
        }

        void CloseMainWindow(object sender, EventArgs e)
        {
            m_mainWindow = null;
        }

        public override void Open(Window parentWindow, StatusBar worker, Action doAfter = null)
        {
            if (m_mainWindow == null || ! m_dataFile.IsUpToDate)
            {
                if (m_dataFile.SupportsProcesses)
                {
                    // Only ETL/ETLX file supported
                    ETLPerfViewData etlDataFile = m_dataFile as ETLPerfViewData;

                    if (etlDataFile == null)
                    {
                        return;
                    }

                    m_traceLog = etlDataFile.GetTraceLog(worker.LogWriter);

                    if (m_traceLog != null)
                    {
                        m_worker = worker;

                        m_traceLog.SelectClrProcess(LaunchViewer);
                    }
                }
            }
            else
            {
                m_mainWindow.Focus();

                if (doAfter != null)
                    doAfter();
            }
        }

        public override void Close() 
        { 
        }
        
        public override ImageSource Icon 
        { 
            get 
            { 
                return GuiApp.MainWindow.Resources["ChartBitmapImage"] as ImageSource; 
            } 
        }
    }
}
