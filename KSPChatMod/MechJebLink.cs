using System;
using System.Reflection;

namespace KSPChatBridge
{
    // P5-4: optional MechJeb2 link by reflection (no compile-time dependency). Every call reports a clear
    // "needs MechJeb" / API mismatch instead of silently falling back to kRPC.
    internal static class MechJebLink
    {
        internal const string Missing = MechJebPolicy.Missing;
        static Type coreType; static bool searched;

        static Type CoreType()
        {
            if (searched) return coreType;
            searched = true;
            foreach (var la in AssemblyLoader.loadedAssemblies)
            {
                try { var t = la.assembly.GetType("MuMech.MechJebCore"); if (t != null) { coreType = t; break; } } catch (Exception) { }
            }
            return coreType;
        }

        internal static object Core(Vessel v)
        {
            var t = CoreType(); if (t == null || v == null) return null;
            foreach (Part p in v.parts) foreach (PartModule m in p.Modules) if (t.IsInstanceOfType(m)) return m;
            return null;
        }

        internal static object Module(object core, string name)
        {
            var mi = core.GetType().GetMethod("GetComputerModule", new[] { typeof(string) });
            if (mi == null) throw new InvalidOperationException("MechJeb API mismatch: GetComputerModule(string)");
            var m = mi.Invoke(core, new object[] { name });
            if (m == null) throw new InvalidOperationException("MechJeb module unavailable: " + name);
            return m;
        }

        static object Member(object o, string name)
        {
            var t = o.GetType();
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p != null ? p.GetValue(o, null) : null;
        }

        /// <summary>Set a plain or EditableDouble/EditableInt member (via .val) on the first matching name.</summary>
        internal static bool SetValue(object o, double value, params string[] names)
        {
            foreach (string n in names)
            {
                var t = o.GetType();
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var p = f == null ? t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                if (f == null && p == null) continue;
                Type mt = f != null ? f.FieldType : p.PropertyType;
                if (mt == typeof(double)) { if (f != null) f.SetValue(o, value); else p.SetValue(o, value, null); return true; }
                if (mt == typeof(float)) { if (f != null) f.SetValue(o, (float)value); else p.SetValue(o, (float)value, null); return true; }
                if (mt == typeof(bool)) { bool b = value != 0; if (f != null) f.SetValue(o, b); else p.SetValue(o, b, null); return true; }
                object ed = f != null ? f.GetValue(o) : p.GetValue(o, null);
                if (ed == null) continue;
                var val = ed.GetType().GetProperty("val") ?? ed.GetType().GetProperty("Val");
                if (val != null) { val.SetValue(ed, Convert.ChangeType(value, val.PropertyType), null); return true; }
            }
            return false;
        }

        internal static void Use(object module, object controller)
        {
            var users = Member(module, "users") ?? Member(module, "Users");
            if (users == null) throw new InvalidOperationException("MechJeb API mismatch: users pool");
            users.GetType().GetMethod("Add").Invoke(users, new[] { controller });
        }

        internal static void Release(object module, object controller)
        {
            var users = Member(module, "users") ?? Member(module, "Users");
            if (users != null) users.GetType().GetMethod("Remove").Invoke(users, new[] { controller });
        }

        internal static void Call(object o, string method, params object[] args)
        {
            foreach (var mi in o.GetType().GetMethods())
                if (mi.Name == method && mi.GetParameters().Length == args.Length) { mi.Invoke(o, args); return; }
            throw new InvalidOperationException("MechJeb API mismatch: " + method);
        }

        internal static object Get(object o, string name) { return Member(o, name); }
    }
}
