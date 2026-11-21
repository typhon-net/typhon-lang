// Copyright (c) 2026 The Typhon contributors.
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace WinUITodo;

/// <summary>
/// The base class of the Typhon App (app.ty), and the only C# in this sample. XAML asks the Application for the types it
/// does not know itself (IXamlMetadataProvider); WinUI's control library (XamlControlsResources, the Fluent styles, the
/// AcrylicBrush they use) answers through XamlControlsXamlMetaDataProvider. In a C# app the XAML compiler generates this
/// for App.xaml. The interface has two GetXamlType overloads, which a Typhon class cannot declare.
/// </summary>
public class XamlApplication : Application, IXamlMetadataProvider
{
    readonly XamlControlsXamlMetaDataProvider _controls = new();

    public IXamlType GetXamlType(Type type) => _controls.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _controls.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _controls.GetXmlnsDefinitions();
}
