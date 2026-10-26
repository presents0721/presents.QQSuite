using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace QQSuite.Core;

/// <summary>
/// 手写的小控件：本仓库的 Razor 编译链不识别 @onclick / @bind（会被输出成普通 HTML），
/// 所以交互元素统一在这里用 RenderTreeBuilder 直接挂 EventCallback。
/// </summary>
public class QQSuiteButton : ComponentBase
{
    /// <summary>switch / link / primary</summary>
    [Parameter]
    public string Kind { get; set; } = "primary";

    [Parameter]
    public bool Checked { get; set; }

    [Parameter]
    public bool Partial { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public EventCallback<MouseEventArgs> OnClick { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "type", "button");
        builder.AddAttribute(2, "class", GetClass());
        if (Disabled)
        {
            builder.AddAttribute(3, "disabled", true);
        }
        if (OnClick.HasDelegate)
        {
            builder.AddAttribute(4, "onclick", OnClick);
        }
        if (Kind == "switch")
        {
            builder.AddMarkupContent(5, "<span></span>");
        }
        else if (ChildContent != null)
        {
            builder.AddContent(6, ChildContent);
        }
        builder.CloseElement();
    }

    private string GetClass()
    {
        if (Kind == "switch")
        {
            return "qqs-switch" + (Checked ? " on" : Partial ? " partial" : string.Empty);
        }
        return Kind == "link" ? "qqs-link" : "qqs-btn";
    }
}

public class QQSuiteInput : ComponentBase
{
    [Parameter]
    public string Value { get; set; } = string.Empty;

    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    [Parameter]
    public string? Placeholder { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "class", "qqs-input");
        builder.AddAttribute(2, "value", Value);
        if (!string.IsNullOrEmpty(Placeholder))
        {
            builder.AddAttribute(3, "placeholder", Placeholder);
        }
        builder.AddAttribute(4, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, HandleInput));
        builder.CloseElement();
    }

    private Task HandleInput(ChangeEventArgs args)
    {
        return ValueChanged.InvokeAsync(args.Value?.ToString() ?? string.Empty);
    }
}
