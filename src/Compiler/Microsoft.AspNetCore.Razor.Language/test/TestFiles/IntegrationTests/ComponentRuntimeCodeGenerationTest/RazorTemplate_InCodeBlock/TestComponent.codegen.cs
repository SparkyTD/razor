﻿// <auto-generated/>
#pragma warning disable 1591
namespace Test
{
    #line hidden
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Components;
    public partial class TestComponent : global::Microsoft.AspNetCore.Components.ComponentBase
    {
        #pragma warning disable 1998
        protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
        {
#nullable restore
#line 1 "x:\dir\subdir\Test\TestComponent.cshtml"
  
    RenderFragment<Person> p = (person) => 

#line default
#line hidden
#nullable disable
            (__builder2) => {
                __builder2.OpenElement(0, "div");
#nullable restore
#line (2,51)-(2,62) 25 "x:\dir\subdir\Test\TestComponent.cshtml"
__builder2.AddContent(1, person.Name);

#line default
#line hidden
#nullable disable
                __builder2.CloseElement();
            }
#nullable restore
#line 2 "x:\dir\subdir\Test\TestComponent.cshtml"
                                                                   ;

#line default
#line hidden
#nullable disable
        }
        #pragma warning restore 1998
#nullable restore
#line 4 "x:\dir\subdir\Test\TestComponent.cshtml"
       
    class Person
    {
        public string Name { get; set; }
    }

#line default
#line hidden
#nullable disable
    }
}
#pragma warning restore 1591
