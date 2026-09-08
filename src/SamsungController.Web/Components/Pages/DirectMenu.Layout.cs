using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SamsungController.Web.Services;

namespace SamsungController.Web.Components.Pages;

public partial class DirectMenu
{
    [Inject] private IJSRuntime LayoutJavaScript { get; set; } = default!;
    private ElementReference ExpertLayoutElement;
    private ElementReference AttachedLayoutElement;
    private string? AttachedLayoutSection;
    private ElementReference MenuToolbarElement, MenuContentElement;
    private bool MenuToolbarAttached, ScrollSectionOnRender;
    private DotNetObjectReference<DirectMenu>? LayoutReference;
    private bool LayoutAttached, LayoutSaving;
    private string? LayoutFeedback;
    private bool LayoutLocked => Snapshot.IsBusy || ActionRunning || LayoutSaving;
    private bool HasArrangedLayout => IpExpertLayout.SupportsSection(Section);
    private IEnumerable<IpExpertGroup> VisibleExpertGroups => IpExpertLayout.Ordered(Snapshot.Menu.Preferences.GroupOrder(Section), Section)
        .Where(group => group.Controls.Any(IsVisible));

    private async Task AttachMenuToolbarAsync()
    {
        try
        {
            if (!MenuToolbarAttached)
            {
                await LayoutJavaScript.InvokeVoidAsync("samsungMenuToolbar.attach", MenuToolbarElement);
                MenuToolbarAttached = true;
            }
            if (ScrollSectionOnRender)
            {
                ScrollSectionOnRender = false;
                await LayoutJavaScript.InvokeVoidAsync("samsungMenuToolbar.scrollToContent", MenuToolbarElement, MenuContentElement);
            }
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
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
