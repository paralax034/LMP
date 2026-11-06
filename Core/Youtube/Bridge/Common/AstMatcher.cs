using Acornima;
using Acornima.Ast;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Provides high-performance matching of Acornima AST nodes against MatchTemplate templates.
/// </summary>
internal static class AstMatcher
{
    /// <summary>
    /// Recursively checks whether an Acornima AST node matches the specified template.
    /// </summary>
    public static bool Matches(Node? node, MatchTemplate? template)
    {
        if (template is null) return true;
        if (node is null) return false;

        if (template.NodeType is not null && !template.NodeType.IsInstanceOfType(node))
        {
            return false;
        }

        if (template.Name is not null)
        {
            if (node is not Identifier id || id.Name != template.Name)
            {
                return false;
            }
        }

        if (template.Value is not null)
        {
            if (node is not Literal lit || !Equals(lit.Value, template.Value))
            {
                return false;
            }
        }

        if (template.Operator is not null)
        {
            var opStr = node switch
            {
                AssignmentExpression ae => MapOperator(ae.Operator),
                BinaryExpression be => MapOperator(be.Operator),
                UpdateExpression ue => MapOperator(ue.Operator),
                UnaryExpression une => MapOperator(une.Operator),
                _ => null
            };

            if (opStr != template.Operator)
            {
                return false;
            }
        }

        if (template.Async.HasValue)
        {
            bool isAsync = node switch
            {
                FunctionDeclaration fd => fd.Async,
                FunctionExpression fe => fe.Async,
                ArrowFunctionExpression afe => afe.Async,
                _ => false
            };
            if (isAsync != template.Async.Value)
            {
                return false;
            }
        }

        if (template.Computed.HasValue)
        {
            if (node is not MemberExpression me || me.Computed != template.Computed.Value)
            {
                return false;
            }
        }

        if (template.Optional.HasValue)
        {
            bool opt = node switch
            {
                MemberExpression me => me.Optional,
                CallExpression ce => ce.Optional,
                _ => false
            };
            if (opt != template.Optional.Value)
            {
                return false;
            }
        }

        if (template.Left is not null)
        {
            Node? leftNode = node switch
            {
                AssignmentExpression ae => ae.Left,
                BinaryExpression be => be.Left,
                _ => null
            };
            if (!Matches(leftNode, template.Left)) return false;
        }

        if (template.Right is not null)
        {
            Node? rightNode = node switch
            {
                AssignmentExpression ae => ae.Right,
                BinaryExpression be => be.Right,
                _ => null
            };
            if (!Matches(rightNode, template.Right)) return false;
        }

        if (template.Object is not null)
        {
            if (node is not MemberExpression me || !Matches(me.Object, template.Object)) return false;
        }

        if (template.Property is not null)
        {
            if (node is not MemberExpression me || !Matches(me.Property, template.Property)) return false;
        }

        if (template.Expression is not null)
        {
            if (node is not ExpressionStatement es || !Matches(es.Expression, template.Expression)) return false;
        }

        if (template.Callee is not null)
        {
            if (node is not CallExpression ce || !Matches(ce.Callee, template.Callee)) return false;
        }

        if (template.Id is not null)
        {
            Node? idNode = node switch
            {
                FunctionDeclaration fd => fd.Id,
                ClassDeclaration cd => cd.Id,
                VariableDeclarator vd => vd.Id,
                _ => null
            };
            if (!Matches(idNode, template.Id)) return false;
        }

        if (template.Init is not null)
        {
            if (node is not VariableDeclarator vd || !Matches(vd.Init, template.Init)) return false;
        }

        if (template.Or is not null)
        {
            bool matchedAny = false;
            for (int i = 0; i < template.Or.Length; i++)
            {
                if (Matches(node, template.Or[i]))
                {
                    matchedAny = true;
                    break;
                }
            }
            if (!matchedAny) return false;
        }

        if (template.AnyKey is not null)
        {
            bool anyKeyMatched = node switch
            {
                VariableDeclaration vd => MatchAnyKey(vd.Declarations, template.AnyKey),
                BlockStatement bs => MatchAnyKey(bs.Body, template.AnyKey),
                Program p => MatchAnyKey(p.Body, template.AnyKey),
                CallExpression ce => MatchAnyKey(ce.Arguments, template.AnyKey),
                _ => MatchAnyKey(node.ChildNodes, template.AnyKey)
            };

            if (!anyKeyMatched) return false;
        }

        if (template.Arguments is not null)
        {
            if (node is not CallExpression ce || ce.Arguments.Count != template.Arguments.Length) return false;

            for (int i = 0; i < template.Arguments.Length; i++)
            {
                if (!Matches(ce.Arguments[i], template.Arguments[i])) return false;
            }
        }

        if (template.ExpectedArgumentsCount.HasValue)
        {
            if (node is not CallExpression ce || ce.Arguments.Count != template.ExpectedArgumentsCount.Value) return false;
        }

        return true;
    }

    private static bool MatchAnyKey<T>(NodeList<T> collection, MatchTemplate[] templates) where T : Node
    {
        for (int i = 0; i < templates.Length; i++)
        {
            var t = templates[i];
            bool matchedAny = false;
            for (int j = 0; j < collection.Count; j++)
            {
                if (Matches(collection[j], t))
                {
                    matchedAny = true;
                    break;
                }
            }
            if (!matchedAny) return false;
        }
        return true;
    }

    private static bool MatchAnyKey(ChildNodes collection, MatchTemplate[] templates)
    {
        for (int i = 0; i < templates.Length; i++)
        {
            var t = templates[i];
            bool matchedAny = false;

            foreach (var child in collection)
            {
                if (Matches(child, t))
                {
                    matchedAny = true;
                    break;
                }
            }

            if (!matchedAny) return false;
        }
        return true;
    }

    private static string? MapOperator(Operator op) => op switch
    {
        Operator.Assignment => "=",
        Operator.AdditionAssignment => "+=",
        Operator.SubtractionAssignment => "-=",
        Operator.MultiplicationAssignment => "*=",
        Operator.DivisionAssignment => "/=",
        Operator.LogicalAnd => "&&",
        Operator.LogicalOr => "||",
        Operator.StrictEquality => "===",
        Operator.StrictInequality => "!==",
        Operator.Equality => "==",
        Operator.Inequality => "!=",
        Operator.Increment => "++",
        Operator.Decrement => "--",
        Operator.LogicalNot => "!",
        _ => null
    };
}