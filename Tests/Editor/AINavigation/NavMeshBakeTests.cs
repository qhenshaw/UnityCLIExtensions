using System.Collections.Generic;
using NUnit.Framework;
using Unity.AI.Navigation;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCLIExtensions.AINavigation.Tests
{
    public class NavMeshBakeTests
    {
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        private NavMeshSurface Make(string name)
        {
            var go = new GameObject(name);
            _made.Add(go);
            return go.AddComponent<NavMeshSurface>();
        }

        [Test]
        public void UnknownSurface_IsNotFoundWithDidYouMean()
        {
            Make("NavTestSurface");
            var none = SurfaceCommands.Resolve("NavTestSurfce", out string error, out ExitCode code);
            Assert.IsNull(none);
            Assert.AreEqual(ExitCode.NotFound, code);
            StringAssert.Contains("No active NavMeshSurface on 'NavTestSurfce'. Did you mean: NavTestSurface?", error);
        }

        [Test]
        public void KnownSurface_ResolvesOnlyInASavedScene()
        {
            var s = Make("NavTestSurface");
            var found = SurfaceCommands.Resolve("navtestsurface", out string error, out ExitCode code);
            if (string.IsNullOrEmpty(s.gameObject.scene.path))
            {
                // Its data would be saved straight into Assets/: refused before anything is cleared or baked.
                Assert.IsNull(found);
                Assert.AreEqual(ExitCode.Failed, code);
                StringAssert.Contains("is in an unsaved scene: save the scene first", error);
            }
            else
            {
                Assert.IsNull(error);
                CollectionAssert.AreEqual(new[] { s }, found);
            }
        }
    }
}
