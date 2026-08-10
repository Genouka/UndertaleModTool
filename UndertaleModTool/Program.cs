#pragma warning disable CA1416 // Validate platform compatibility

using System;
using System.Collections.Generic;
using log4net;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.CodeAnalysis;
using UndertaleModTool.Localization;

namespace UndertaleModTool
{
    public static class Program
    {
        public static string GetExecutableDirectory()
        {
            return Path.GetDirectoryName(Environment.ProcessPath);
        }

        // https://stackoverflow.com/questions/1025843/merging-dlls-into-a-single-exe-with-wpf
        [STAThread]
        public static void Main()
        {
            try
            {
                AppDomain currentDomain = default(AppDomain);
                currentDomain = AppDomain.CurrentDomain;
                // Handler for unhandled exceptions.
                currentDomain.UnhandledException += GlobalUnhandledExceptionHandler;
                // Handler for exceptions in threads behind forms.
                System.Windows.Forms.Application.ThreadException += GlobalThreadExceptionHandler;
                App.Main();
            }
            catch (Exception e)
            {
                var filePath = Path.Join(GetExecutableDirectory(), "crash.txt");
                File.WriteAllText(filePath, e.ToString());
                MessageBox.Show(string.Format(LocalizationSource.GetString("Msg_UnhandledException"), e.ToString()));
                try
                {
                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    };
                    Process.Start(processStartInfo);
                }catch(Exception ignored){}
            }
        }
        private static void GlobalUnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = default(Exception);
            ex = (Exception)e.ExceptionObject;
            ILog log = LogManager.GetLogger(typeof(Program));
            log.Error(ex.Message + "\n" + ex.StackTrace);
            var filePath = Path.Join(GetExecutableDirectory(), "crash2.txt");
            File.WriteAllText(filePath, (ex.ToString() + "\n" + ex.Message + "\n" + ex.StackTrace));
            // Open crash2.txt
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };
                Process.Start(processStartInfo);
            }catch(Exception ignored){}
        }

        private static void GlobalThreadExceptionHandler(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Exception ex = default(Exception);
            ex = e.Exception;
            ILog log = LogManager.GetLogger(typeof(Program)); //Log4NET
            log.Error(ex.Message + "\n" + ex.StackTrace);
            var filePath = Path.Join(GetExecutableDirectory(), "crash3.txt");
            File.WriteAllText(filePath, (ex.Message + "\n" + ex.StackTrace));
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };
                Process.Start(processStartInfo);
            }catch(Exception ignored){}
        }
    }
}

#pragma warning restore CA1416 // Validate platform compatibility
