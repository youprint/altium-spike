// SdkDump -- print the public types and members of an assembly, straight
// from its metadata. No Altium installation or runtime loading needed.
//
// The Altium C# SDK has no usable documentation and is not a rename of the
// DelphiScript API, so this is how a member name gets confirmed BEFORE code is
// written against it. Guessed names are the most common way a change here
// compiles and then fails silently at runtime.
//
// Usage (from this folder):
//
//   dotnet run -- <assembly.dll> [type-regex] [members]
//
//   dotnet run -- ../../Assemblies/Altium.SDK.Interfaces.dll "IPCB_Polygon(Helper)?$" members
//   dotnet run -- ../../Assemblies/Altium.SDK.Interfaces.dll "LayerUtils" members
//   dotnet run -- ../../Assemblies/Altium.SDK.Interfaces.dll "Helper$"   (type names only)
//
// ALWAYS DUMP THE HELPER TOO. Many of the useful calls are not on the
// interface: they are extension-style wrappers on a class named
// <Interface>Helper in the same assembly -- IPCB_PolygonHelper.GetState_Segments,
// IPCB_LayerUtilsHelper.FromString and .MechanicalLayer. The interface itself
// carries an Internal_ twin that returns a different, less useful type.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

class Prov : ISignatureTypeProvider<string, object>
{
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawKind)
    { var t = r.GetTypeDefinition(h); return r.GetString(t.Name); }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawKind)
    { var t = r.GetTypeReference(h); return r.GetString(t.Name); }
    public string GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte rawKind)
    { return r.GetTypeSpecification(h).DecodeSignature(this, g); }
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object g, int i) => "!!" + i;
    public string GetGenericTypeParameter(object g, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool isRequired) => u;
    public string GetPinnedType(string e) => e;
    public string GetFunctionPointerType(MethodSignature<string> si) => "fnptr";
}

class P
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
        {
            Console.WriteLine("usage: SdkDump <assembly.dll> [type-regex] [members]");
            return 1;
        }
        string path = args[0];
        if (!File.Exists(path)) { Console.Error.WriteLine("not found: " + path); return 1; }
        var filter = args.Length > 1 ? new Regex(args[1], RegexOptions.IgnoreCase) : null;
        bool membersToo = args.Length > 2 && args[2] == "members";

        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var r = pe.GetMetadataReader();
        var prov = new Prov();

        foreach (var h in r.TypeDefinitions)
        {
            var t = r.GetTypeDefinition(h);
            string ns = r.GetString(t.Namespace);
            string nm = r.GetString(t.Name);
            string full = string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;
            if (filter != null && !filter.IsMatch(full)) continue;

            var attrs = t.Attributes;
            bool isIface = (attrs & TypeAttributes.Interface) != 0;
            bool isPublic = (attrs & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic;
            if (!isPublic) continue;

            var ifaces = t.GetInterfaceImplementations()
                .Select(i => r.GetInterfaceImplementation(i))
                .Select(i => HandleName(r, i.Interface))
                .Where(s => s != null).ToArray();

            // The base class is listed first. It is how "is this a WinForms
            // Form or a WPF control?" gets answered without loading anything.
            string baseName = t.BaseType.IsNil ? null : HandleName(r, t.BaseType);
            if (baseName == "Object") baseName = null;
            var bases = (baseName != null ? new[] { baseName } : new string[0]).Concat(ifaces).ToArray();
            Console.WriteLine((isIface ? "interface " : "class ") + full
                + (bases.Length > 0 ? " : " + string.Join(", ", bases) : ""));

            if (!membersToo) continue;

            foreach (var fh in t.GetFields())
            {
                var fd = r.GetFieldDefinition(fh);
                var facc = fd.Attributes & FieldAttributes.FieldAccessMask;
                if (facc != FieldAttributes.Public) continue;
                string ftype;
                try { ftype = fd.DecodeSignature(prov, null); } catch { ftype = "?"; }
                string fmods = ((fd.Attributes & FieldAttributes.Static) != 0 ? "static " : "")
                             + ((fd.Attributes & FieldAttributes.Literal) != 0 ? "const " : "");
                Console.WriteLine("    field " + fmods + ftype + " " + r.GetString(fd.Name));
            }

            foreach (var mh in t.GetMethods())
            {
                var m = r.GetMethodDefinition(mh);
                var acc = m.Attributes & MethodAttributes.MemberAccessMask;
                if (acc == MethodAttributes.Private) continue;
                string vis = acc switch
                {
                    MethodAttributes.Public => "public",
                    MethodAttributes.Family => "protected",
                    MethodAttributes.FamORAssem => "protected internal",
                    MethodAttributes.Assembly => "internal",
                    _ => acc.ToString()
                };
                if ((m.Attributes & MethodAttributes.Static) != 0) vis += " static";
                if ((m.Attributes & MethodAttributes.Abstract) != 0) vis += " ABSTRACT";
                else if ((m.Attributes & MethodAttributes.Virtual) != 0) vis += " virtual";

                string mn = r.GetString(m.Name);
                MethodSignature<string> sig;
                try { sig = m.DecodeSignature(prov, null); } catch { continue; }

                // Parameter NAMES too: two adjacent String parameters (a
                // view name and a caption, say) cannot be told apart by type.
                var names = new string[sig.ParameterTypes.Length];
                foreach (var ph in m.GetParameters())
                {
                    var pd = r.GetParameter(ph);
                    int i = pd.SequenceNumber - 1;          // 0 is the return value
                    if (i >= 0 && i < names.Length) names[i] = r.GetString(pd.Name);
                }
                var ps = sig.ParameterTypes.Select((pt, i) => names[i] == null ? pt : pt + " " + names[i]);
                Console.WriteLine("    " + vis + " " + sig.ReturnType + " " + mn
                    + "(" + string.Join(", ", ps) + ")");
            }
            Console.WriteLine();
        }
        return 0;
    }

    static string HandleName(MetadataReader r, EntityHandle h)
    {
        if (h.Kind == HandleKind.TypeReference)
        { var x = r.GetTypeReference((TypeReferenceHandle)h); return r.GetString(x.Name); }
        if (h.Kind == HandleKind.TypeDefinition)
        { var x = r.GetTypeDefinition((TypeDefinitionHandle)h); return r.GetString(x.Name); }
        return null;
    }
}
