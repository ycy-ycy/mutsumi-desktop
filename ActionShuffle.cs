using System;

namespace Mutsumi.Desktop
{
    // Clicks and quiet moments use independent cycles, so autonomous actions
    // don't consume any of the five interactions the user can discover.
    internal sealed class ActionShuffle
    {
        private readonly PetAction[] cycle;
        private int remaining;
        private PetAction last;

        public ActionShuffle(PetAction[] actions) => cycle = (PetAction[])actions.Clone();

        public PetAction Draw(Random random)
        {
            if (remaining == 0)
            {
                for (int i = cycle.Length - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (cycle[i], cycle[j]) = (cycle[j], cycle[i]);
                }
                int end = cycle.Length - 1;
                if (cycle[end] == last)
                    (cycle[end], cycle[0]) = (cycle[0], cycle[end]);
                remaining = cycle.Length;
            }
            return last = cycle[--remaining];
        }
    }
}
