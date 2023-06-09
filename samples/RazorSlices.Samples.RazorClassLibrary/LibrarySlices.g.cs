﻿using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;

// Generated

namespace RazorSlices.Samples.RazorClassLibrary.Slices;

public sealed class FromLibrary
#if NET7_0_OR_GREATER
    : IRazorSliceProxy<RazorSliceHttpResult>
#endif
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private static readonly Type _type = Type.GetType("AspNetCoreGeneratedDocument.Slices_FromLibrary, RazorSlices.Samples.RazorClassLibrary");
    //private static readonly ConstructorInfo _ctor = _type.GetConstructor(Type.EmptyTypes)!;
    private static readonly SliceFactory _factory = RuntimeFeature.IsDynamicCodeCompiled
        ? RazorSliceFactory.GetSliceFactory(_type)
        : static () =>
            //(RazorSlice)_ctor.Invoke(null);
            (RazorSlice)Activator.CreateInstance(_type);

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "AspNetCoreGeneratedDocument.Slices_FromLibrary", "RazorSlices.Samples.RazorClassLibrary")]
    public static RazorSliceHttpResult Create() => RazorSlice.CreateHttpResult(_factory);
}

public sealed class Todos
#if NET7_0_OR_GREATER
    : IRazorSliceProxy<RazorSliceHttpResult>
#endif
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private static readonly Type _type = Type.GetType("AspNetCoreGeneratedDocument.Slices_Todos, RazorSlices.Samples.RazorClassLibrary");
    //private static readonly ConstructorInfo _ctor = _type.GetConstructor(Type.EmptyTypes)!;
    private static SliceFactory _factory = RuntimeFeature.IsDynamicCodeCompiled
        ? RazorSliceFactory.GetSliceFactory(_type)
        : static () =>
            //(RazorSlice) _ctor.Invoke(null);
            (RazorSlice)Activator.CreateInstance(_type);

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "AspNetCoreGeneratedDocument.Slices_Todos", "RazorSlices.Samples.RazorClassLibrary")]
    public static RazorSliceHttpResult Create() => RazorSlice.CreateHttpResult(_factory);
}