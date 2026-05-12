using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim.Logic
{
    // Boolean IR expression. S2.1 uses only Const / NodeRef / And / Or / Complex (conductance conditions);
    // S2.2+ adds Not / Mux / Hold / Prev for next-state expressions. Records → value equality (handy for tests).
    //
    // And()/Or() do trivial normalisation only — flatten the top-level AND/OR, drop 1/0, dedup, sort the
    // leaves by their text form (so And(a,b) == And(b,a) and the same logical condition compares equal).
    // That's NOT full CSE/folding (that's S3) — just enough that DriveInfo.PullDown is canonical-ish.
    internal abstract record Expr
    {
        public static readonly Expr True    = new ConstExpr(true);
        public static readonly Expr False   = new ConstExpr(false);
        public static readonly Expr Complex = new ComplexExpr();

        public static Expr Node(int id)  => new NodeRefExpr(id);
        public static Expr Const(bool v) => v ? True : False;

        public static Expr Not(Expr a) =>
            a is ComplexExpr ? a :
            a is ConstExpr c ? Const(!c.Value) :
            a is NotExpr n   ? n.Operand :
            new NotExpr(a);

        public static Expr And(Expr a, Expr b)
        {
            if (a is ComplexExpr || b is ComplexExpr) return Complex;
            var leaves = new List<Expr>();
            void Collect(Expr e) { if (e is AndExpr x) { Collect(x.L); Collect(x.R); } else leaves.Add(e); }
            Collect(a); Collect(b);
            if (leaves.Any(e => e is ConstExpr { Value: false })) return False;
            leaves = leaves.Where(e => e is not ConstExpr { Value: true }).Distinct().OrderBy(e => e.Pretty(), StringComparer.Ordinal).ToList();
            if (leaves.Count == 0) return True;
            Expr r = leaves[0];
            for (int i = 1; i < leaves.Count; i++) r = new AndExpr(r, leaves[i]);
            return r;
        }
        public static Expr Or(Expr a, Expr b)
        {
            if (a is ComplexExpr || b is ComplexExpr) return Complex;
            var leaves = new List<Expr>();
            void Collect(Expr e) { if (e is OrExpr x) { Collect(x.L); Collect(x.R); } else leaves.Add(e); }
            Collect(a); Collect(b);
            if (leaves.Any(e => e is ConstExpr { Value: true })) return True;
            leaves = leaves.Where(e => e is not ConstExpr { Value: false }).Distinct().OrderBy(e => e.Pretty(), StringComparer.Ordinal).ToList();
            if (leaves.Count == 0) return False;
            Expr r = leaves[0];
            for (int i = 1; i < leaves.Count; i++) r = new OrExpr(r, leaves[i]);
            return r;
        }
        public static Expr AndAll(IEnumerable<Expr> xs) { Expr r = True;  foreach (var x in xs) r = And(r, x); return r; }
        public static Expr OrAll (IEnumerable<Expr> xs) { Expr r = False; foreach (var x in xs) r = Or (r, x); return r; }

        public bool IsComplex => this is ComplexExpr;
        public bool IsConst(bool v) => this is ConstExpr c && c.Value == v;

        public string Pretty() => this switch
        {
            ConstExpr c   => c.Value ? "1" : "0",
            NodeRefExpr n => $"n{n.Id}",
            NotExpr n     => $"!{n.Operand.Pretty()}",
            AndExpr a     => $"({a.L.Pretty()} & {a.R.Pretty()})",
            OrExpr o      => $"({o.L.Pretty()} | {o.R.Pretty()})",
            MuxExpr m     => $"({m.Cond.Pretty()} ? {m.A.Pretty()} : {m.B.Pretty()})",
            HoldExpr h    => $"hold(n{h.Id})",
            PrevExpr p    => $"prev(n{p.Id})",
            ComplexExpr   => "<complex>",
            _             => "?",
        };
    }
    internal sealed record ConstExpr(bool Value) : Expr;
    internal sealed record NodeRefExpr(int Id) : Expr;                   // node Id's *current* value
    internal sealed record NotExpr(Expr Operand) : Expr;                 // (S2.2+)
    internal sealed record AndExpr(Expr L, Expr R) : Expr;
    internal sealed record OrExpr(Expr L, Expr R) : Expr;
    internal sealed record MuxExpr(Expr Cond, Expr A, Expr B) : Expr;    // (S2.2+) Cond ? A : B
    internal sealed record HoldExpr(int Id) : Expr;                      // (S2.2+) keep node Id's value (parasitic capacitance)
    internal sealed record PrevExpr(int Id) : Expr;                      // (S2.2+) node Id's previous-cycle value (loop-breaking)
    internal sealed record ComplexExpr : Expr;                           // sentinel: couldn't extract a clean expression
}
