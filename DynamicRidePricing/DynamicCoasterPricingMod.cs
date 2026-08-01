using System;
using System.Collections.Generic;
using UnityEngine;

namespace DynamicRidePricing
{
    public sealed class DynamicCoasterPricingMod : AbstractMod
    {
        private const string LogPrefix = "[DynamicRidePricing]";
        private static GameObject hostObject;

        public override string getName()
        {
            return "Dynamic Ride Pricing";
        }

        public override string getDescription()
        {
            return "Automatically adjusts all ride prices from excitement, queue demand, weather, and decaying popularity. Shops are excluded.";
        }

        public override string getVersionNumber()
        {
            return "1.0.1";
        }

        public override string getIdentifier()
        {
            return "DynamicRidePricing";
        }

        public override bool isMultiplayerModeCompatible()
        {
            // Multiplayer command synchronization has not been validated yet.
            return false;
        }

        public override void onEnabled()
        {
            if (hostObject != null)
            {
                UnityEngine.Object.Destroy(hostObject);
            }

            hostObject = new GameObject("DynamicRidePricing.Host");
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            hostObject.AddComponent<DynamicPricingBehaviour>();
        }

        public override void onDisabled()
        {
            if (hostObject != null)
            {
                UnityEngine.Object.Destroy(hostObject);
                hostObject = null;
            }
        }
    }

    internal sealed class DynamicPricingBehaviour : MonoBehaviour
    {
        private const string LogPrefix = "[DynamicRidePricing]";
        private const float FirstPassDelaySeconds = 15f;
        private const float PassIntervalSeconds = 60f;
        private const float MinimumPrice = 1f;
        private const float MaximumPrice = 30f;
        private const float MaximumChangePerPass = 0.50f;
        private const float PopularityPreviousWeight = 0.90f;
        private const float PopularityObservationWeight = 0.10f;

        private readonly Dictionary<int, RideHistory> historyByRide = new Dictionary<int, RideHistory>();
        private float secondsUntilPass = FirstPassDelaySeconds;

        private void Update()
        {
            secondsUntilPass -= Time.unscaledDeltaTime;
            if (secondsUntilPass > 0f)
            {
                return;
            }

            secondsUntilPass = PassIntervalSeconds;
            RunPricingPass();
        }

        private void RunPricingPass()
        {
            GameController controller = GameController.Instance;
            if (controller == null || !controller.isPlayingPark || controller.park == null)
            {
                return;
            }

            Park park = controller.park;
            WeatherController weather = park.weatherController;
            bool raining = weather != null && weather.IsRaining;
            bool storming = weather != null && weather.IsStorming;
            float temperature = weather != null ? weather.Temperature : 20f;
            // Shops live in Park.shops, not Park.getAttractions(), so iterating this
            // collection includes tracked and flat rides while deliberately excluding shops.
            foreach (Attraction ride in park.getAttractions())
            {
                // Untested or closed rides are observed but never repriced.
                float excitement = ride.getExcitementRatingForUI();
                if (ride.state != Attraction.State.OPENED || excitement <= 0f)
                {
                    continue;
                }

                try
                {
                    EvaluateRide(ride, excitement, raining, storming, temperature);
                }
                catch (Exception exception)
                {
                    Debug.LogError(LogPrefix + " Failed to evaluate " + SafeRideName(ride) + ": " + exception);
                }
            }
        }

        private void EvaluateRide(
            Attraction ride,
            float excitement,
            bool raining,
            bool storming,
            float temperature)
        {
            int rideId = ride.GetInstanceID();
            int queueGuests = Math.Max(0, ride.getQueueingGuestsCount());
            int currentCustomers = Math.Max(0, ride.customersCurrentMonthCount);
            int missedCustomers = Math.Max(0, ride.missedCustomersCurrentMonthCount);

            RideHistory history;
            if (!historyByRide.TryGetValue(rideId, out history))
            {
                history = new RideHistory
                {
                    LastCustomerCount = currentCustomers,
                    LastMissedCustomerCount = missedCustomers,
                    Popularity = 0.50f
                };
                historyByRide.Add(rideId, history);
            }

            int newCustomers = currentCustomers >= history.LastCustomerCount
                ? currentCustomers - history.LastCustomerCount
                : currentCustomers;
            history.LastCustomerCount = currentCustomers;

            int newMissedCustomers = missedCustomers >= history.LastMissedCustomerCount
                ? missedCustomers - history.LastMissedCustomerCount
                : missedCustomers;
            history.LastMissedCustomerCount = missedCustomers;

            // Fresh riders and queue interest raise the observation. Guests who reject
            // the ride lower it. With no activity, the EMA naturally decays.
            float observedPopularity = Mathf.Clamp01(
                (newCustomers + queueGuests * 0.25f - newMissedCustomers * 0.25f) / 10f);
            history.Popularity =
                history.Popularity * PopularityPreviousWeight
                + observedPopularity * PopularityObservationWeight;

            // Parkitect exposes this rating on a 0-1 scale. Multiplying by ten gives
            // a sensible currency base (for example, 0.72 excitement -> 7.20).
            float excitementBasePrice = Mathf.Clamp(excitement * 10f, MinimumPrice, MaximumPrice);
            float queueMultiplier = CalculateQueueMultiplier(queueGuests);
            float weatherMultiplier = CalculateWeatherMultiplier(ride, raining, storming, temperature);
            float popularityMultiplier = Mathf.Lerp(0.85f, 1.15f, history.Popularity);

            float rawTarget = excitementBasePrice
                * queueMultiplier
                * weatherMultiplier
                * popularityMultiplier;
            float roundedTarget = RoundToTenCents(Mathf.Clamp(rawTarget, MinimumPrice, MaximumPrice));

            float oldPrice = ride.entranceFee;
            float smoothedTarget = Mathf.MoveTowards(oldPrice, roundedTarget, MaximumChangePerPass);
            float newPrice = RoundToTenCents(smoothedTarget);
            bool shouldChange = Mathf.Abs(newPrice - oldPrice) >= 0.09f;

            if (!shouldChange)
            {
                return;
            }

            new AttractionChangeEntranceFeeCommand(ride, newPrice).run();
        }

        private static float CalculateQueueMultiplier(int queueGuests)
        {
            if (queueGuests == 0) return 0.80f;
            if (queueGuests < 5) return 0.90f;
            if (queueGuests < 10) return 1.00f;
            if (queueGuests < 20) return 1.05f;
            if (queueGuests < 40) return 1.15f;
            return 1.25f;
        }

        private static float CalculateWeatherMultiplier(
            Attraction ride,
            bool raining,
            bool storming,
            float temperature)
        {
            float multiplier = 1f;

            if (raining)
            {
                float rainProtection = Mathf.Clamp01(ride.getRainProtection());
                multiplier *= Mathf.Lerp(0.85f, 0.98f, rainProtection);
            }

            if (storming)
            {
                multiplier *= 0.92f;
            }

            if (ride.temperaturePreference == TemperaturePreference.HOT)
            {
                multiplier *= Mathf.Lerp(0.90f, 1.10f, Mathf.InverseLerp(10f, 30f, temperature));
            }
            else if (ride.temperaturePreference == TemperaturePreference.COLD)
            {
                multiplier *= Mathf.Lerp(1.10f, 0.90f, Mathf.InverseLerp(10f, 30f, temperature));
            }

            return multiplier;
        }

        private static float RoundToTenCents(float value)
        {
            return Mathf.Round(value * 10f) / 10f;
        }

        private static string SafeRideName(Attraction ride)
        {
            string rideName = ride.getCustomizedName();
            return string.IsNullOrEmpty(rideName) ? ride.getName() : rideName;
        }

        private sealed class RideHistory
        {
            public int LastCustomerCount;
            public int LastMissedCustomerCount;
            public float Popularity;
        }
    }
}
