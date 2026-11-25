using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityCLIExtensions.UIToolkit;
using UnityEngine;

namespace UnityCLIExtensions.Tests
{
    public class ValueTextTests
    {
        [Test]
        public void Parse_ReadsNumbersVectorsColoursAndEnums()
        {
            Assert.AreEqual(1.5f, ValueText.Parse("1.5", typeof(float)));
            Assert.AreEqual(new Vector3(1, 2, 3), ValueText.Parse("[1,2,3]", typeof(Vector3)));
            Assert.AreEqual(new Vector2(4, 4), ValueText.Parse("4", typeof(Vector2)), "One number fills every component.");
            Assert.AreEqual(new Color(1, 0.5f, 0, 1), ValueText.Parse("[1,0.5,0]", typeof(Color)));
            Assert.AreEqual(Color.red, ValueText.Parse("#FF0000", typeof(Color)));
            Assert.AreEqual(FilterMode.Point, ValueText.Parse("point", typeof(FilterMode)));
            Assert.AreEqual(true, ValueText.Parse("1", typeof(bool)));
        }

        [Test]
        public void Parse_ReadsCurvesAndGradients()
        {
            var curve = (AnimationCurve)ValueText.Parse("[[0,0],[0.5,1],[1,0]]", typeof(AnimationCurve));
            Assert.AreEqual(3, curve.length);
            Assert.AreEqual(1f, curve.keys[1].value);
            var gradient = (Gradient)ValueText.Parse("[[0,1,0,0],[1,0,0,1,0.5]]", typeof(Gradient));
            Assert.AreEqual(2, gradient.colorKeys.Length);
            Assert.AreEqual(0.5f, gradient.alphaKeys[1].alpha);
        }

        [Test]
        public void Parse_RejectsTheWrongNumberOfComponents()
        {
            Assert.Throws<ArgumentException>(() => ValueText.Parse("[1,2]", typeof(Vector3)));
        }

        [Test]
        public void Format_RoundTripsWhatParseReads()
        {
            foreach (var (text, type) in new[] { ("[1,2,3]", typeof(Vector3)), ("0.25", typeof(float)), ("[[0,0],[1,2]]", typeof(AnimationCurve)) })
                Assert.AreEqual(text, ValueText.Format(ValueText.Parse(text, type)));
        }
    }

    public class TemplatedEvalTests
    {
        [Test]
        public void Fill_ReplacesEveryPlaceholder()
        {
            var vars = TemplatedEval.ParseVars("item=Sword;count=3");
            Assert.AreEqual("Give(\"Sword\", 3); // Sword", TemplatedEval.Fill("Give(\"{{item}}\", {{ count }}); // {{item}}", vars));
        }

        [Test]
        public void Fill_NamesUnfilledPlaceholders()
        {
            var e = Assert.Throws<ArgumentException>(() => TemplatedEval.Fill("{{a}} {{b}}", TemplatedEval.ParseVars("a=1")));
            StringAssert.Contains("{{b}}", e.Message);
        }

        [Test]
        public void ParseVars_SplitsOnSemicolonsOrCommas()
        {
            Assert.AreEqual("x,y", TemplatedEval.ParseVars("list=x,y;n=1")["list"], "With a ';' present, commas stay in values.");
            Assert.AreEqual(2, TemplatedEval.ParseVars("a=1,b=2").Count);
            Assert.Throws<ArgumentException>(() => TemplatedEval.ParseVars("nokey"));
        }
    }

    public class SelectorTests
    {
        private sealed class Node
        {
            public string type, name;
            public string[] classes = Array.Empty<string>();
            public Node parent;
            public List<Node> children = new List<Node>();
            public Node Add(Node c) { c.parent = this; children.Add(c); return this; }
        }

        private static List<Node> Match(Node root, string selector) =>
            new UITKSelector(selector).Match(root, n => n.children, n => n.parent, n => n.type, n => n.name, n => n.classes);

        private static Node Tree()
        {
            var start = new Node { type = "Button", name = "start", classes = new[] { "primary" } };
            var quit = new Node { type = "Button", name = "quit" };
            var menu = new Node { type = "VisualElement", name = "menu" }.Add(start).Add(quit);
            var nested = new Node { type = "VisualElement", name = "panel" }.Add(new Node { type = "Button", name = "deep" });
            menu.Add(nested);
            return new Node { type = "Root" }.Add(menu);
        }

        [Test]
        public void Matches_TypeNameAndClassCombined()
        {
            Assert.AreEqual("start", Match(Tree(), "Button.primary#start").Single().name);
            Assert.AreEqual(3, Match(Tree(), "Button").Count);
        }

        [Test]
        public void ChildAndDescendant_Differ()
        {
            Assert.AreEqual(2, Match(Tree(), "#menu > Button").Count, "Only direct children.");
            Assert.AreEqual(3, Match(Tree(), "#menu Button").Count, "Any depth.");
        }
    }
}
