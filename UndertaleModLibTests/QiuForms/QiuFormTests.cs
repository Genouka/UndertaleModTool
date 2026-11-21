// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Linq;
using QiuForms;
using Xunit;

namespace UndertaleModLibTests.QiuForms;

public class QiuFormTests
{
    [Fact]
    public void BuildersAddFieldsInOrder()
    {
        QiuForm form = new("Test");
        form.Text("Name", "spr_");
        form.Number("Speed", 1);
        form.Check("Special", true);

        Assert.Equal(3, form.EnumerateFields().Count());
        Assert.Equal(["Name", "Speed", "Special"], form.EnumerateFields().Select(f => f.Label));
    }

    [Fact]
    public void FindReturnsElementById()
    {
        QiuForm form = new("Test");
        QiuTextBox name = form.Text("Name", "spr_", id: "name");
        QiuGroup group = form.Group("Advanced", id: "advanced");
        QiuCheckBox flag = group.Check("Flag", id: "flag");

        Assert.Same(name, form.Find("name"));
        Assert.Same(group, form.Find("advanced"));
        Assert.Same(flag, form.FindField("flag"));
        Assert.Null(form.Find("missing"));
    }

    [Fact]
    public void RequiredFieldRejectsEmptyValue()
    {
        QiuForm form = new("Test");
        QiuTextBox name = form.Text("Name");
        name.IsRequired();

        Assert.False(form.Validate());
        Assert.Same(name, form.FirstInvalid);
        Assert.Equal(form.RequiredMessage, name!.Error);

        name.SetValueFromHost("spr_thing");
        Assert.True(form.Validate());
        Assert.Null(name.Error);
        Assert.Null(form.FirstInvalid);
    }

    [Fact]
    public void CustomValidatorRunsAndReportsMessage()
    {
        QiuForm form = new("Test");
        QiuTextBox name = form.Text("Name", "abc");
        name.Validate(field => ((QiuTextBox)field).Value.Length > 3 ? null : "Too short.");

        Assert.False(form.Validate());
        Assert.Equal("Too short.", name.Error);

        name.SetValueFromHost("abcd");
        Assert.True(form.Validate());
    }

    [Fact]
    public void NumberFieldClampsAndValidatesRange()
    {
        QiuForm form = new("Test");
        QiuNumberBox number = form.Number("Value", 50, minimum: 0, maximum: 10);

        Assert.Equal(10, number.Value);

        number.SetValueFromHost(-5);
        Assert.Equal(0, number.Value);

        number.AllowEmpty = false;
        number.SetValueFromHost(double.NaN);
        Assert.False(form.Validate());
        Assert.Equal(number.EmptyMessage, number.Error);
    }

    [Fact]
    public void NumberFieldRoundsToDecimalsOrInteger()
    {
        QiuNumberBox fractional = new("F", 1.2345, decimals: 2);
        Assert.Equal(1.23, fractional.Value);

        QiuNumberBox integer = new("I", 1.6, integer: true);
        Assert.Equal(2, integer.Value);
    }

    [Fact]
    public void ChoiceClampsSelectionAndExposesItem()
    {
        QiuChoice choice = new("Mode", ["A", "B", "C"], selectedIndex: 10);

        Assert.Equal(2, choice.Value);
        Assert.Equal("C", choice.SelectedItem);

        choice.SetValueFromHost(-1);
        Assert.Equal(0, choice.Value);
        Assert.Equal("A", choice.SelectedItem);
    }

    [Fact]
    public void EnabledWhenFollowsOtherFields()
    {
        QiuForm form = new("Test");
        QiuCheckBox special = form.Check("Special", false);
        QiuNumberBox speed = form.Number("Speed", 1);
        speed.EnabledWhen(() => special.Value);

        Assert.False(speed.EffectiveIsEnabled);

        special.SetValueFromHost(true);
        Assert.True(speed.EffectiveIsEnabled);
    }

    [Fact]
    public void DisabledOrHiddenFieldsAreNotValidated()
    {
        QiuForm form = new("Test");
        QiuCheckBox enable = form.Check("Enable", false);
        QiuTextBox hidden = form.Text("Hidden");
        hidden.IsRequired();
        hidden.VisibleWhen(() => enable.Value);

        Assert.True(form.Validate());

        enable.SetValueFromHost(true);
        Assert.False(form.Validate());
    }

    [Fact]
    public void ChangedIsRaisedOnlyWhenValueChanges()
    {
        QiuTextBox text = new("Name", "a");
        int changes = 0;
        text.Changed += (_, _) => changes++;

        text.SetValueFromHost("a");
        Assert.Equal(0, changes);

        text.SetValueFromHost("b");
        Assert.Equal(1, changes);
    }

    [Fact]
    public void TextBoxHonoursMaxLength()
    {
        QiuTextBox text = new("Name") { MaxLength = 3 };
        text.SetValueFromHost("abcdef");
        Assert.Equal("abc", text.Value);
    }

    [Fact]
    public void ChildVisibilityIsInheritedFromPanel()
    {
        QiuForm form = new("Test");
        QiuCheckBox enable = form.Check("Enable", false);
        QiuGroup group = form.Group("Group");
        QiuTextBox field = group.Text("Field");
        group.VisibleWhen(() => enable.Value);

        Assert.False(field.EffectiveIsVisible);

        enable.SetValueFromHost(true);
        Assert.True(field.EffectiveIsVisible);
    }

    [Fact]
    public void ListBoxKeepsSelectionInRange()
    {
        QiuListBox list = new();
        list.SetItems(["a", "b", "c"]);
        list.SelectedIndex = 10;
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal("c", list.SelectedItem);

        list.SetItems(["x"]);
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal("x", list.SelectedItem);

        list.SetItems([]);
        Assert.Equal(-1, list.SelectedIndex);
        Assert.Null(list.SelectedItem);
    }

    [Fact]
    public void TreeViewReplacesItemsAndClearsSelection()
    {
        QiuTreeView tree = new("Sprites");
        tree.SetItems(["spr_a", "spr_b"]);
        Assert.Equal(2, tree.Nodes.Count);
        Assert.Equal("Sprites", tree.Root.Text);

        tree.SelectedNode = tree.Nodes[1];
        Assert.Equal("spr_b", tree.SelectedText);

        tree.SetItems(["spr_c"]);
        Assert.Single(tree.Nodes);
        Assert.Null(tree.SelectedText);
    }

    [Fact]
    public void SliderClampsValue()
    {
        QiuSlider slider = new("FPS", 24, 1, 60);
        Assert.Equal(24, slider.Value);

        slider.SetValueFromHost(100);
        Assert.Equal(60, slider.Value);

        slider.SetValueFromHost(-5);
        Assert.Equal(1, slider.Value);
    }

    [Fact]
    public void CanvasRaisesPointerEvents()
    {
        QiuCanvas canvas = new();
        QiuPointerEventArgs? pressed = null;
        canvas.PointerPressed += args => pressed = args;

        canvas.RaisePointerPressed(new QiuPointerEventArgs(3, 4, QiuPointerButton.Primary, true));

        Assert.NotNull(pressed);
        Assert.Equal(3, pressed!.Value.X);
        Assert.Equal(4, pressed.Value.Y);
        Assert.True(pressed.Value.IsPressed);
    }

    [Fact]
    public void HostWithoutSupportThrows()
    {
        IQiuFormsHost host = new NoUiHost();
        Assert.False(host.SupportsQiuForms);
        Assert.Throws<QiuFormUnavailableException>(() => host.ShowQiuForm(new QiuForm("Test")));
    }

    private sealed class NoUiHost : IQiuFormsHost
    {
    }
}
