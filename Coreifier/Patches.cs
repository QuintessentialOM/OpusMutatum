using MonoMod;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Runtime.Loader;
using System.Security;
using System.Security.Permissions;

namespace Coreifier;

public static class Patches {
    // don't know what these are even for. maybe monomod uses them ???
    // they were the only non-mod-specific ones in everest so i suppose they're important

    #region ModuleBuilder

    private const string AssemblyBuilderFName = "System.Reflection.Emit.AssemblyBuilder";
    private const string ModuleBuilderFName = "System.Reflection.Emit.ModuleBuilder";

    [MonoModLinkFrom($"{ModuleBuilderFName} {AssemblyBuilderFName}::DefineDynamicModule(System.String,System.Boolean)")]
    public static ModuleBuilder DefineDynamicModule(AssemblyBuilder builder, string name, bool emitSymInfo)
        => builder.DefineDynamicModule(name);

    [MonoModLinkFrom($"{ModuleBuilderFName} {AssemblyBuilderFName}::DefineDynamicModule(System.String,System.String)")]
    public static ModuleBuilder DefineDynamicModule(AssemblyBuilder builder, string name, string file)
        => throw new NotSupportedException("Saving ModuleBuilder output to files is no longer supported");

    [MonoModLinkFrom($"{ModuleBuilderFName} {AssemblyBuilderFName}::DefineDynamicModule(System.String,System.String,System.Boolean)")]
    public static ModuleBuilder DefineDynamicModule(AssemblyBuilder builder, string name, string file, bool emitSymInfo)
        => throw new NotSupportedException("Saving ModuleBuilder output to files is no longer supported");

    #endregion

    #region AssemblyBuilder

    public enum SecurityContextSource {
        CurrentAppDomain = 0,
        CurrentAssembly = 1
    }

    private const string AssemblyNameFName = "System.Reflection.AssemblyName";
    private const string AssemblyBuilderAccessFName = "System.Reflection.Emit.AssemblyBuilderAccess";
    private const string CustomAttributeBuilderFName = "System.Reflection.Emit.CustomAttributeBuilder";

    [MonoModLinkFrom($"{AssemblyBuilderFName} System.AppDomain::DefineDynamicAssembly({AssemblyNameFName},{AssemblyBuilderAccessFName},System.String,System.Boolean,System.Collections.Generic.IEnumerable`1<{CustomAttributeBuilderFName}>)")]
    public static AssemblyBuilder DefineDynamicAssembly(AppDomain domain, AssemblyName name,
        AssemblyBuilderAccess access, string dir, bool isSynchronized, IEnumerable<CustomAttributeBuilder> asmAttrs) {
        using (AssemblyLoadContext.EnterContextualReflection(Assembly.GetCallingAssembly()))
            return AssemblyBuilder.DefineDynamicAssembly(name, access, asmAttrs);
    }

    [MonoModLinkFrom($"{AssemblyBuilderFName} System.AppDomain::DefineDynamicAssembly({AssemblyNameFName},{AssemblyBuilderAccessFName},System.Collections.Generic.IEnumerable`1<{CustomAttributeBuilderFName}>,NETCoreifier.AppDomain/SecurityContextSource)")]
    public static AssemblyBuilder DefineDynamicAssembly(AppDomain domain, AssemblyName name,
        AssemblyBuilderAccess access, IEnumerable<CustomAttributeBuilder> asmAttrs, SecurityContextSource ctxSrc) {
        using (AssemblyLoadContext.EnterContextualReflection(Assembly.GetCallingAssembly()))
            return AssemblyBuilder.DefineDynamicAssembly(name, access, asmAttrs);
    }

    [MonoModLinkFrom($"{AssemblyBuilderFName} System.AppDomain::DefineDynamicAssembly({AssemblyNameFName},{AssemblyBuilderAccessFName},System.String)")]
    public static AssemblyBuilder DefineDynamicAssembly(AppDomain domain, AssemblyName name,
        AssemblyBuilderAccess access, string dir) {
        using (AssemblyLoadContext.EnterContextualReflection(Assembly.GetCallingAssembly()))
            return AssemblyBuilder.DefineDynamicAssembly(name, access);
    }

    [MonoModLinkFrom($"{AssemblyBuilderFName} System.AppDomain::DefineDynamicAssembly({AssemblyNameFName},{AssemblyBuilderAccessFName})")]
    public static AssemblyBuilder DefineDynamicAssembly(AppDomain domain, AssemblyName name,
        AssemblyBuilderAccess access) {
        using (AssemblyLoadContext.EnterContextualReflection(Assembly.GetCallingAssembly()))
            return AssemblyBuilder.DefineDynamicAssembly(name, access);
    }

    #endregion

    #region DeclarativeSecurity
    #pragma warning disable SYSLIB0003 // Code security is no longer honored

    private const string EmitNSpace = "System.Reflection.Emit";
    private const string SecurityActionFName = "System.Security.Permissions.SecurityAction";
    private const string PermissionSetFName = "System.Security.PermissionSet";

    [MonoModLinkFrom($"System.Void {EmitNSpace}.TypeBuilder::AddDeclarativeSecurity({SecurityActionFName},{PermissionSetFName})")]
    public static void AddDeclarativeSecurity(TypeBuilder builder, SecurityAction action, PermissionSet perms) { }

    [MonoModLinkFrom($"System.Void {EmitNSpace}.MethodBuilder::AddDeclarativeSecurity({SecurityActionFName},{PermissionSetFName})")]
    public static void AddDeclarativeSecurity(MethodBuilder builder, SecurityAction action, PermissionSet perms) { }

    [MonoModLinkFrom($"System.Void {EmitNSpace}.ConstructorBuilder::AddDeclarativeSecurity({SecurityActionFName},{PermissionSetFName})")]
    public static void AddDeclarativeSecurity(ConstructorBuilder builder, SecurityAction action, PermissionSet perms) { }

    #pragma warning restore SYSLIB0003
    #endregion
}
