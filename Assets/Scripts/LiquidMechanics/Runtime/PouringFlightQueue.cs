using System.Collections.Generic;
using UnityEngine;

namespace MultiBar.Liquids
{
    /// <summary>Tracks extracted liquid while it travels from a mouth to a receiver.</summary>
    public sealed class PouringFlightQueue
    {
        private sealed class Flight
        {
            public LiquidContainer source;
            public LiquidContainer target;
            public LiquidPayload payload;
            public float remainingTime;
        }

        private readonly List<Flight> flights = new List<Flight>(32);

        public int Count => flights.Count;

        public void Enqueue(LiquidContainer source, LiquidContainer target, LiquidPayload payload, float delay)
        {
            if (payload == null || payload.Volume <= 0.0001f) return;
            if (target == null)
            {
                if (source != null) payload.AddTo(source);
                return;
            }

            flights.Add(new Flight
            {
                source = source,
                target = target,
                payload = payload,
                remainingTime = Mathf.Max(0f, delay)
            });
        }

        public void Advance(float deltaTime)
        {
            if (deltaTime <= 0f || flights.Count == 0) return;

            int write = 0;
            for (int read = 0; read < flights.Count; read++)
            {
                Flight flight = flights[read];
                flight.remainingTime -= deltaTime;
                if (flight.remainingTime > 0.00001f)
                {
                    flights[write++] = flight;
                    continue;
                }
                Deliver(flight);
            }

            if (write < flights.Count)
                flights.RemoveRange(write, flights.Count - write);
        }

        public void Flush()
        {
            if (flights.Count == 0) return;
            Flight[] pending = flights.ToArray();
            flights.Clear();
            for (int i = 0; i < pending.Length; i++) Deliver(pending[i]);
        }

        public float PendingVolumeFor(LiquidContainer target)
        {
            float volume = 0f;
            for (int i = 0; i < flights.Count; i++)
                if (flights[i].target == target) volume += flights[i].payload.Volume;
            return volume;
        }

        private static void Deliver(Flight flight)
        {
            if (flight.target != null)
            {
                LiquidPayload remainder = flight.payload.AddTo(flight.target);
                if (remainder.Volume > 0.0001f && flight.source != null)
                    remainder.AddTo(flight.source);
            }
            else if (flight.source != null)
            {
                flight.payload.AddTo(flight.source);
            }
        }
    }
}
