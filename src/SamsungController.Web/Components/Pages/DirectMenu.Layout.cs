using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SamsungController.Web.Services;

namespace SamsungController.Web.Components.Pages;

public partial class DirectMenu
{
    [Inject] private IJSRuntime LayoutJavaScript { get; set; } = default!;
    [CascadingParameter] public Layout.MainLayout? PageLayout { get; set; }
    private ElementReference ExpertLayoutElement;
    private ElementReference AttachedLayoutElement;
    private string? AttachedLayoutSection;
    private ElementReference MenuToolbarElement, MenuContentElement;
    private bool MenuToolbarAttached, ScrollSectionOnRender, SwitchingSection;
    private DotNetObjectReference<DirectMenu>? LayoutReference;
    private bool LayoutAttached, LayoutSaving;
    private string? LayoutFeedback;
    private bool LayoutLocked => Snapshot.IsBusy || ActionRunning || LayoutSaving;
    private bool HasArrangedLayout => IpExpertLayout.SupportsSection(Section);
    private IEnumerable<IpExpertGroup> VisibleExpertGroups => IpExpertLayout.Ordered(Snapshot.Menu.Preferences.GroupOrder(Section), Section)
        .Where(group => group.Controls.Any(IsVisible));

    private async Task<bool> AttachMenuToolbarAsync()
    {
        try
        {
            if (!MenuToolbarAttached)
            {
                var savedSection = await LayoutJavaScript.InvokeAsync<string?>("samsungMenuToolbar.attach", MenuToolbarElement, MenuContentElement, Section);
                MenuToolbarAttached = true;
                ScrollSectionOnRender = true;
                if (savedSection != Section && IpMenuCatalog.Sections.Any(section => section.Id == savedSection))
                {
                    Section = savedSection!;
                    PageLayout?.SetMenuHelpSection(Section);
                    await InvokeAsync(StateHasChanged);
                    return true; // Render the remembered section before restoring its position or reading anything.
                }
            }
            if (ScrollSectionOnRender)
            {
                ScrollSectionOnRender = false;
                await LayoutJavaScript.InvokeVoidAsync("samsungMenuToolbar.restorePosition", MenuToolbarElement, MenuContentElement, Section);
            }
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        PageLayout?.SetMenuHelpSection(Section);
        return false;
    }

    private async Task SelectSectionAsync(string section)
    {
        if (section == Section || SwitchingSection || Snapshot.IsBusy || LayoutSaving) return;
        SwitchingSection = true;
        try
        {
            // Capture before replacing the outgoing DOM. Restore after the next render
            // so shorter sections cannot overwrite a long section's position.
            if (MenuToolbarAttached) await LayoutJavaScript.InvokeVoidAsync("samsungMenuToolbar.savePosition", MenuToolbarElement);
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        finally { SwitchingSection = false; }
        Section = section;
        ScrollSectionOnRender = true;
        LayoutFeedback = null;
        Error = null;
        PageLayout?.SetMenuHelpSection(Section);
    }

    private async Task AttachExpertLayoutAsync()
    {
        if (LayoutAttached && AttachedLayoutSection != Section)
        {
            try { await LayoutJavaScript.InvokeVoidAsync("samsungExpertLayout.detach", AttachedLayoutElement); }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
            LayoutAttached = false;
            AttachedLayoutSection = null;
        }
        if (!HasArrangedLayout || LayoutAttached) return;
        LayoutReference ??= DotNetObjectReference.Create(this);
        AttachedLayoutElement = ExpertLayoutElement;
        AttachedLayoutSection = Section;
        LayoutAttached = true;
        try
        {
            await LayoutJavaScript.InvokeVoidAsync("samsungExpertLayout.attach", AttachedLayoutElement, LayoutReference);
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { LayoutFeedback = "Box dragging could not load. Reload this page to rearrange settings."; await InvokeAsync(StateHasChanged); }
    }

    [JSInvokable]
    public Task<bool> MoveMenuGroupAsync(string section, string source, string target, bool after) => section != Section
        ? Task.FromResult(false) // A delayed drag callback must never reorder a newly selected section.
        : ChangeExpertLayoutAsync(() => Controller.MoveMenuGroupAsync(section, source, target, after),
            "Layout saved. Linked controls stay together; nothing was sent to the TV.");

    private Task ResetExpertLayoutAsync() => ChangeExpertLayoutAsync(() => Controller.ResetMenuLayoutAsync(Section),
        "Default layout restored. TV settings and pending edits are unchanged.");

    private async Task<bool> ChangeExpertLayoutAsync(Func<Task> change, string message)
    {
        if (!HasArrangedLayout || LayoutLocked) return false;
        LayoutSaving = true;
        try
        {
            await change();
            Snapshot = Controller.GetSnapshot();
            LayoutFeedback = message;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { LayoutFeedback = "Layout was not saved: " + error.Message; return false; }
        finally { LayoutSaving = false; await InvokeAsync(StateHasChanged); }
    }

    public async ValueTask DisposeAsync()
    {
        Controller.Changed -= Refresh;
        try
        {
            if (MenuToolbarAttached) await LayoutJavaScript.InvokeVoidAsync("samsungMenuToolbar.detach", MenuToolbarElement);
            if (LayoutAttached) await LayoutJavaScript.InvokeVoidAsync("samsungExpertLayout.detach", AttachedLayoutElement);
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        finally { LayoutReference?.Dispose(); }
    }
}
