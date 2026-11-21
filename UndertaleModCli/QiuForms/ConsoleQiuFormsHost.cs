using System;
using System.Globalization;
using QiuForms;

namespace UndertaleModCli;

/// <summary>
/// Text-mode renderer for <see cref="QiuForm"/>s, so scripts that ask for parameters also work
/// without a GUI.
/// </summary>
public static class ConsoleQiuFormsHost
{
    /// <summary>Prompts for every field of <paramref name="form"/> and returns how it was closed.</summary>
    public static QiuDialogResult Show(QiuForm form, Func<string?> readLine)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(readLine);

        Console.WriteLine();
        Console.WriteLine("========== " + form.Title + " ==========");
        if (!string.IsNullOrEmpty(form.Message))
            Console.WriteLine(form.Message);

        PrintListsAndTrees(form.Root);

        while (true)
        {
            foreach (QiuField field in form.EnumerateFields())
            {
                if (!field.EffectiveIsVisible || !field.EffectiveIsEnabled)
                    continue;

                if (!Prompt(field, readLine))
                {
                    form.Result = QiuDialogResult.Cancel;
                    return form.Result;
                }
            }

            if (form.Validate())
            {
                form.Result = QiuDialogResult.Submit;
                return form.Result;
            }

            Console.Error.WriteLine(form.FirstInvalid?.Error ?? "Invalid input.");
            Console.Write("Try again? (Y/N): ");
            string? answer = readLine();
            if (answer is null || (!answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                                   && !answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                form.Result = QiuDialogResult.Cancel;
                return form.Result;
            }
        }
    }

    /// <summary>Prints the items of every list and tree in the form, depth-first.</summary>
    private static void PrintListsAndTrees(QiuElement element)
    {
        switch (element)
        {
            case QiuListBox listBox when listBox.Items.Count > 0:
                Console.WriteLine((listBox.Caption ?? "List") + ":");
                foreach (string item in listBox.Items)
                    Console.WriteLine($"  - {item}");
                break;

            case QiuTreeView treeView when treeView.Nodes.Count > 0:
                Console.WriteLine((treeView.Caption ?? treeView.Root.Text) + ":");
                foreach (QiuTreeNode node in treeView.Nodes)
                    Console.WriteLine($"  - {node.Text}");
                break;

            case QiuPanel panel:
                foreach (QiuElement child in panel.Children)
                    PrintListsAndTrees(child);
                break;
        }
    }

    private static bool Prompt(QiuField field, Func<string?> readLine)
    {
        switch (field)
        {
            case QiuTextBox textBox:
            {
                Console.Write($"{field.Label}: ");
                string? input = readLine();
                if (input is null)
                    return false;

                // An empty line keeps the current value, like the number and choice prompts.
                if (input.Length > 0 || textBox.Value.Length == 0)
                    textBox.SetValueFromHost(input);

                return true;
            }

            case QiuNumberBox numberBox:
            {
                while (true)
                {
                    Console.Write($"{field.Label}" +
                                  (double.IsNaN(numberBox.Value) ? "" : $" [{FormatNumber(numberBox)}]") + ": ");
                    string? input = readLine();
                    if (input is null)
                        return false;

                    if (input.Length == 0)
                        return true;

                    if (double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out double value))
                    {
                        numberBox.SetValueFromHost(value);
                        return true;
                    }

                    Console.Error.WriteLine("Please enter a number.");
                }
            }

            case QiuCheckBox checkBox:
            {
                Console.Write($"{field.Label} (Y/N) [{(checkBox.Value ? "Y" : "N")}]: ");
                string? input = readLine();
                if (input is null)
                    return false;

                if (input.Trim().Length > 0)
                {
                    if (input.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                        || input.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
                        checkBox.SetValueFromHost(true);
                    else if (input.Trim().Equals("n", StringComparison.OrdinalIgnoreCase)
                             || input.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
                        checkBox.SetValueFromHost(false);
                }

                return true;
            }

            case QiuChoice choice:
            {
                Console.WriteLine(field.Label + ":");
                for (int i = 0; i < choice.Items.Count; i++)
                    Console.WriteLine($"  {i + 1}. {choice.Items[i]}");

                while (true)
                {
                    Console.Write($"Select 1-{choice.Items.Count} [{choice.Value + 1}]: ");
                    string? input = readLine();
                    if (input is null)
                        return false;

                    if (input.Trim().Length == 0)
                        return true;

                    if (int.TryParse(input, out int selected) && selected >= 1 && selected <= choice.Items.Count)
                    {
                        choice.SetValueFromHost(selected - 1);
                        return true;
                    }

                    Console.Error.WriteLine("Please enter one of the listed numbers.");
                }
            }

            case QiuSlider slider:
            {
                while (true)
                {
                    Console.Write($"{field.Label} ({slider.Minimum}-{slider.Maximum}) [{slider.Value}]: ");
                    string? input = readLine();
                    if (input is null)
                        return false;

                    if (input.Trim().Length == 0)
                        return true;

                    if (int.TryParse(input, out int value) && value >= slider.Minimum && value <= slider.Maximum)
                    {
                        slider.SetValueFromHost(value);
                        return true;
                    }

                    Console.Error.WriteLine("Please enter a number in the listed range.");
                }
            }

            default:
                // Images and layout-only elements have nothing to ask for.
                return true;
        }
    }

    private static string FormatNumber(QiuNumberBox field)
    {
        string format = field.Integer
            ? "0"
            : field.Decimals <= 0 ? "0" : "0." + new string('0', field.Decimals);

        return field.Value.ToString(format, CultureInfo.CurrentCulture);
    }
}