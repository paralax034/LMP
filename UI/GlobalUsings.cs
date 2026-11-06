// System namespaces
global using System;
global using System.Collections.Generic;
global using System.Threading;
global using System.Threading.Tasks;

// MVVM generators (CommunityToolkit.Mvvm)
global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;

// Core namespaces (LMP.Core)
global using LMP.Core.Models;
global using LMP.Core.Services;
global using LMP.Core.ViewModels;
global using LMP.Core.Helpers;
global using LMP.Core.Helpers.Extensions;
global using LMP.Core.Audio;
global using Log = LMP.Core.Logger.Log;

// UI namespaces (LMP)
global using LMP.UI.Models;
global using LMP.UI.Services;
global using LMP.UI.Helpers;
global using LMP.UI.ViewModels;

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LMP.Core")]
[assembly: InternalsVisibleTo("LMP.Tests")]