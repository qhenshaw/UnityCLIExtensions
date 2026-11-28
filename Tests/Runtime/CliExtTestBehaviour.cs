using UnityEngine;

namespace UnityCLIExtensions.Tests
{
    /// <summary>A StateMachineBehaviour for the add_behaviour tests: one public and one [SerializeField] field.</summary>
    public class CliExtTestBehaviour : StateMachineBehaviour
    {
        public float damage = 1;
        [SerializeField] private int hits;
        public int Hits => hits;
    }
}
