using System;
using System.Collections.Generic;
using Parkitect.UI;
using UnityEngine;

namespace VIPGuestsAndInspectors
{
    public sealed class VIPGuestsAndInspectorsMod : AbstractMod
    {
        private static GameObject hostObject;

        public override string getName() { return "Special Guest Visits"; }

        public override string getDescription()
        {
            return "Adds occasional Health Inspectors, Safety Inspectors, Thrill Reviewers, Influencers, and Park Auditors with distinct park evaluations.";
        }

        public override string getVersionNumber() { return "1.0.0"; }
        public override string getIdentifier() { return "VIPGuestsAndInspectors"; }
        public override bool isMultiplayerModeCompatible() { return false; }

        public override void onEnabled()
        {
            if (hostObject != null)
            {
                UnityEngine.Object.Destroy(hostObject);
            }

            hostObject = new GameObject("VIPGuestsAndInspectors.Host");
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            hostObject.AddComponent<SpecialVisitBehaviour>();
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

    internal sealed class SpecialVisitBehaviour : MonoBehaviour
    {
        private const float FirstVisitMinimum = 60f;
        private const float FirstVisitMaximum = 180f;
        private const float QuietPeriodMinimum = 300f;
        private const float QuietPeriodMaximum = 600f;
        private const float VisitDuration = 180f;
        private const float EvaluationInterval = 1f;
        private const float InspectionRadius = 5f;
        private const float DirtyShopThreshold = 0.55f;
        private const float LowMaintenanceBudget = 0.50f;
        private const float HealthFine = 250f;
        private const float SafetyFine = 400f;
        private const float ThrillThreshold = 0.75f;
        private const float InfluencerThreshold = 0.60f;
        private const int ThrillBoost = 150;
        private const int InfluencerBoost = 75;

        private readonly System.Random random = new System.Random();
        private Park activePark;
        private EventManager activeEvents;
        private VIPRecord activeVisit;
        private VIPRole? lastRole;
        private float nextVisitTimer;
        private float visitTimer;
        private float evaluationTimer;
        private bool scheduleStarted;
        private int appliedPopularityBoost;
        private float boostTimer;
        private GUIStyle negativeLabelStyle;
        private GUIStyle positiveLabelStyle;
        private GUIStyle shadowLabelStyle;

        private void Update()
        {
            Park park = GameController.Instance == null ? null : GameController.Instance.park;
            if (park != activePark)
            {
                SwitchPark(park);
            }

            if (activePark == null)
            {
                return;
            }

            EnsureSubscribed();
            UpdatePopularityBoost();

            if (GameController.Instance == null || !GameController.Instance.isPlayingPark)
            {
                return;
            }

            if (!scheduleStarted)
            {
                nextVisitTimer = RandomRange(FirstVisitMinimum, FirstVisitMaximum);
                scheduleStarted = true;
            }

            if (activeVisit == null)
            {
                nextVisitTimer -= Time.deltaTime;
                if (nextVisitTimer <= 0f)
                {
                    StartRandomVisit();
                }

                return;
            }

            if (activeVisit.Guest == null)
            {
                EndVisit();
                return;
            }

            visitTimer -= Time.deltaTime;
            if (visitTimer <= 0f)
            {
                EndVisit();
                return;
            }

            evaluationTimer -= Time.deltaTime;
            if (evaluationTimer <= 0f)
            {
                evaluationTimer = EvaluationInterval;
                EvaluateActiveVisit();
            }
        }

        private void SwitchPark(Park park)
        {
            Unsubscribe();
            RemovePopularityBoost();
            activeVisit = null;
            activePark = park;
            scheduleStarted = false;
            nextVisitTimer = 0f;
            visitTimer = 0f;
            evaluationTimer = EvaluationInterval;

            if (activePark != null)
            {
                EnsureSubscribed();
            }
        }

        private void StartRandomVisit()
        {
            IList<Guest> guests = activePark.getGuests();
            if (guests == null || guests.Count == 0)
            {
                nextVisitTimer = 30f;
                return;
            }

            Guest guest = guests[random.Next(guests.Count)];
            if (guest == null)
            {
                nextVisitTimer = 15f;
                return;
            }

            VIPRole role = PickRole();
            activeVisit = new VIPRecord(guest, role);
            lastRole = role;
            visitTimer = VisitDuration;
            evaluationTimer = 0f;
            AnnounceVisit(activeVisit);

            if (role == VIPRole.ParkAuditor)
            {
                RunParkAudit(activeVisit);
            }
        }

        private VIPRole PickRole()
        {
            VIPRole role;
            do
            {
                role = (VIPRole)random.Next(0, 5);
            }
            while (lastRole.HasValue && role == lastRole.Value);

            return role;
        }

        private void AnnounceVisit(VIPRecord record)
        {
            switch (record.Role)
            {
                case VIPRole.HealthInspector:
                    record.Guest.setName("Health", "Inspector", "INSPECTOR");
                    Notify("Health Inspector Visit", "An inspector is checking nearby shops and paths for hygiene problems.");
                    break;
                case VIPRole.SafetyInspector:
                    record.Guest.setName("Safety", "Inspector", "SAFETY");
                    Notify("Safety Inspection", "A safety inspector is checking nearby rides for breakdowns and underfunded maintenance.");
                    break;
                case VIPRole.ThrillReviewer:
                    record.Guest.setName("Thrill", "Reviewer", "REVIEWER");
                    Notify("Thrill Reviewer Visit", "A strong review can temporarily increase interest in the park.");
                    break;
                case VIPRole.Influencer:
                    record.Guest.setName("Park", "Influencer", "INFLUENCER");
                    Notify("Influencer Visit", "An influencer is looking for an exciting ride to share with their followers.");
                    break;
                case VIPRole.ParkAuditor:
                    record.Guest.setName("Park", "Auditor", "AUDITOR");
                    Notify("Park Auditor Visit", "An auditor is reviewing the park's latest operating result.");
                    break;
            }
        }

        private void EvaluateActiveVisit()
        {
            if (activeVisit.Role == VIPRole.HealthInspector)
            {
                EvaluateHealthInspector(activeVisit);
            }
            else if (activeVisit.Role == VIPRole.SafetyInspector)
            {
                EvaluateSafetyInspector(activeVisit);
            }
        }

        private void EvaluateHealthInspector(VIPRecord record)
        {
            if (record.Citations >= 3)
            {
                return;
            }

            Vector3 position = record.Guest.currentPosition;
            Vomit[] vomit = UnityEngine.Object.FindObjectsOfType<Vomit>();
            for (int i = 0; i < vomit.Length && record.Citations < 3; i++)
            {
                Vomit puddle = vomit[i];
                if (puddle == null || !puddle.gameObject.activeInHierarchy)
                {
                    continue;
                }

                int id = puddle.GetInstanceID();
                if (record.CitedObjects.Add(id) && HorizontalDistance(position, puddle.transform.position) <= InspectionRadius)
                {
                    record.Citations++;
                    ApplyMoney(-HealthFine);
                    Notify("Hygiene Fine: -\u00A3250", "The Health Inspector found vomit on a path.");
                }
            }

            IList<Shop> shops = activePark.getShops();
            for (int i = 0; i < shops.Count && record.Citations < 3; i++)
            {
                Shop shop = shops[i];
                if (shop == null || shop.needsCleaningPercentage < DirtyShopThreshold)
                {
                    continue;
                }

                int id = shop.GetInstanceID();
                if (record.CitedObjects.Add(id) && HorizontalDistance(position, shop.transform.position) <= InspectionRadius)
                {
                    record.Citations++;
                    ApplyMoney(-HealthFine);
                    Notify("Hygiene Fine: -\u00A3250", "The Health Inspector found a dirty shop: " + shop.getCustomizedName() + ".");
                }
            }
        }

        private void EvaluateSafetyInspector(VIPRecord record)
        {
            if (record.Citations >= 2)
            {
                return;
            }

            Vector3 position = record.Guest.currentPosition;
            IList<Attraction> attractions = activePark.getAttractions();
            for (int i = 0; i < attractions.Count && record.Citations < 2; i++)
            {
                Attraction attraction = attractions[i];
                if (attraction == null || HorizontalDistance(position, attraction.transform.position) > InspectionRadius)
                {
                    continue;
                }

                bool unsafeCondition = attraction.getCondition() != Attraction.Condition.OPERATIONAL;
                bool underfunded = attraction.maintenanceBudget < LowMaintenanceBudget;
                int id = attraction.GetInstanceID();
                if ((unsafeCondition || underfunded) && record.CitedObjects.Add(id))
                {
                    record.Citations++;
                    ApplyMoney(-SafetyFine);
                    string reason = unsafeCondition ? "was not operational" : "had an underfunded maintenance budget";
                    Notify("Safety Fine: -\u00A3400", attraction.getCustomizedName() + " " + reason + ".");
                }
            }
        }

        private void RunParkAudit(VIPRecord record)
        {
            List<MonthlyTransactions> months = activePark.parkInfo == null ? null : activePark.parkInfo.getMonthlyTransactions();
            if (months == null || months.Count == 0)
            {
                Notify("Audit Complete", "There was not enough financial history for a full audit.");
                return;
            }

            float operatingProfit = months[months.Count - 1].getOperatingProfit();
            if (operatingProfit >= 0f)
            {
                ApplyMoney(300f);
                Notify("Audit Reward: +\u00A3300", "Healthy operating finances earned the park an efficiency award.");
            }
            else
            {
                ApplyMoney(-150f);
                Notify("Audit Fee: -\u00A3150", "The latest operating result was negative and required additional review.");
            }

            record.HasTriggered = true;
        }

        private void OnGuestLeftAttraction(Guest guest, Attraction attraction)
        {
            if (activeVisit == null || guest == null || attraction == null || activeVisit.Guest != guest || activeVisit.HasTriggered)
            {
                return;
            }

            float excitement = attraction.getExcitementRatingForUI();
            if (activeVisit.Role == VIPRole.ThrillReviewer && excitement >= ThrillThreshold)
            {
                activeVisit.HasTriggered = true;
                ApplyPopularityBoost(ThrillBoost, 120f);
                Notify("Excellent Review", attraction.getCustomizedName() + " impressed the reviewer. Park interest is boosted for two minutes.");
            }
            else if (activeVisit.Role == VIPRole.Influencer && excitement >= InfluencerThreshold)
            {
                activeVisit.HasTriggered = true;
                ApplyPopularityBoost(InfluencerBoost, 90f);
                Notify("Influencer Feature", attraction.getCustomizedName() + " was shared with followers. Park interest is temporarily boosted.");
            }
        }

        private void ApplyPopularityBoost(int amount, float duration)
        {
            RemovePopularityBoost();
            activePark.advertisementGuestIncreasement += amount;
            appliedPopularityBoost = amount;
            boostTimer = duration;
        }

        private void UpdatePopularityBoost()
        {
            if (appliedPopularityBoost == 0)
            {
                return;
            }

            boostTimer -= Time.deltaTime;
            if (boostTimer <= 0f)
            {
                RemovePopularityBoost();
            }
        }

        private void RemovePopularityBoost()
        {
            if (activePark != null && appliedPopularityBoost != 0)
            {
                activePark.advertisementGuestIncreasement = Math.Max(0, activePark.advertisementGuestIncreasement - appliedPopularityBoost);
            }

            appliedPopularityBoost = 0;
            boostTimer = 0f;
        }

        private void ApplyMoney(float amount)
        {
            if (activePark != null && activePark.parkInfo != null)
            {
                activePark.parkInfo.moneyTransaction(amount, MonthlyTransactions.Transaction.REWARD);
            }
        }

        private void EndVisit()
        {
            activeVisit = null;
            visitTimer = 0f;
            nextVisitTimer = RandomRange(QuietPeriodMinimum, QuietPeriodMaximum);
        }

        private void OnGuestLeftPark(Guest guest)
        {
            if (activeVisit != null && guest == activeVisit.Guest)
            {
                EndVisit();
            }
        }

        private void OnGUI()
        {
            if (activePark == null || activeVisit == null || activeVisit.Guest == null)
            {
                return;
            }

            EnsureGUIStyles();

            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = UnityEngine.Object.FindObjectOfType<Camera>();
            }

            if (camera == null)
            {
                return;
            }

            Vector3 screen = camera.WorldToScreenPoint(activeVisit.Guest.currentPosition + new Vector3(0f, 2.2f, 0f));
            if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
            {
                return;
            }

            string label = GetRoleName(activeVisit.Role).ToUpperInvariant();
            Rect labelRect = new Rect(screen.x - 90f, Screen.height - screen.y - 13f, 180f, 26f);
            Rect shadowRect = new Rect(labelRect.x + 2f, labelRect.y + 2f, labelRect.width, labelRect.height);
            GUI.Box(labelRect, GUIContent.none);
            GUI.Label(shadowRect, label, shadowLabelStyle);
            GUI.Label(labelRect, label, IsPositiveRole(activeVisit.Role) ? positiveLabelStyle : negativeLabelStyle);
        }

        private void EnsureGUIStyles()
        {
            if (negativeLabelStyle != null)
            {
                return;
            }

            negativeLabelStyle = new GUIStyle(GUI.skin.label);
            negativeLabelStyle.alignment = TextAnchor.MiddleCenter;
            negativeLabelStyle.fontSize = 16;
            negativeLabelStyle.fontStyle = FontStyle.Bold;
            negativeLabelStyle.normal.textColor = new Color(1f, 0.2f, 0.12f, 1f);

            positiveLabelStyle = new GUIStyle(negativeLabelStyle);
            positiveLabelStyle.normal.textColor = new Color(0.1f, 0.9f, 1f, 1f);

            shadowLabelStyle = new GUIStyle(negativeLabelStyle);
            shadowLabelStyle.normal.textColor = Color.black;
        }

        private static string GetRoleName(VIPRole role)
        {
            switch (role)
            {
                case VIPRole.HealthInspector: return "Health Inspector";
                case VIPRole.SafetyInspector: return "Safety Inspector";
                case VIPRole.ThrillReviewer: return "Thrill Reviewer";
                case VIPRole.Influencer: return "Influencer";
                default: return "Park Auditor";
            }
        }

        private static bool IsPositiveRole(VIPRole role)
        {
            return role == VIPRole.ThrillReviewer || role == VIPRole.Influencer;
        }

        private float RandomRange(float minimum, float maximum)
        {
            return minimum + ((float)random.NextDouble() * (maximum - minimum));
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt((x * x) + (z * z));
        }

        private static void Notify(string title, string message)
        {
            if (SystemNotificationManager.Instance != null)
            {
                SystemNotificationManager.Instance.spawnNotification(title, message);
            }

        }

        private void EnsureSubscribed()
        {
            if (activeEvents == null && EventManager.Exists)
            {
                activeEvents = EventManager.Instance;
                activeEvents.OnGuestLeftAttraction += OnGuestLeftAttraction;
                activeEvents.OnGuestLeftPark += OnGuestLeftPark;
            }
        }

        private void Unsubscribe()
        {
            if (activeEvents != null)
            {
                activeEvents.OnGuestLeftAttraction -= OnGuestLeftAttraction;
                activeEvents.OnGuestLeftPark -= OnGuestLeftPark;
                activeEvents = null;
            }
        }

        private void OnDestroy()
        {
            Unsubscribe();
            RemovePopularityBoost();
        }

        private enum VIPRole
        {
            HealthInspector,
            SafetyInspector,
            ThrillReviewer,
            Influencer,
            ParkAuditor
        }

        private sealed class VIPRecord
        {
            public VIPRecord(Guest guest, VIPRole role)
            {
                Guest = guest;
                Role = role;
            }

            public Guest Guest { get; private set; }
            public VIPRole Role { get; private set; }
            public HashSet<int> CitedObjects { get; } = new HashSet<int>();
            public int Citations { get; set; }
            public bool HasTriggered { get; set; }
        }
    }
}
