using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Linq;
using UnityEngine;
using KSP;
//using ModularFI;

namespace DeadlyReentry
{
    class ModuleAeroReentry : PartModule
    {
        [KSPField]
        public bool leaveTemp = false;

        [KSPField]
        public double maxOperationalTemp = -1;

        [KSPField]
        public double maxSkinOperationalTemp = -1;

        public bool is_debugging;
        
        [KSPField(isPersistant = false, guiActive = false, guiName = "Acceleration", guiUnits = " G",   guiFormat = "F3")]
        public double displayGForce;
        
        [KSPField(isPersistant = false, guiActive = false, guiName = "Damage", guiUnits = "",   guiFormat = "G")]
        public string displayDamage;
        
        [KSPField(isPersistant = false, guiActive = false, guiName = "Cumulative G", guiUnits = "", guiFormat = "F0")]
        public double gExperienced = 0;

        /*
        private ModularFlightIntegrator fi;
        public ModularFlightIntegrator FI
        {
            get
            {
                if ((object)fi == null)
                    fi = vessel.gameObject.GetComponent<ModularFlightIntegrator>();
                return fi;
            }
            set
            {
                fi = value;
            }
        }
        
        public ModularFlightIntegrator.PartThermalData ptd;
        */
        private double lastGForce = 0;
        
        [KSPField(isPersistant = true)]
        public bool dead;
        
        [KSPField]
        public float gTolerance = -1;
        
        [KSPField(isPersistant = true)]
        public float crashTolerance = 8;
        
        [KSPField(isPersistant = true)]
        public float damage = 0;

        private bool isCompatible = true;
        
        private bool is_on_fire = false;
        private bool is_gforce_fx_playing = false;
        
        private bool is_engine = false;

        protected double recordedHeatLoad = 0.0;
        protected double maximumRecordedHeat = 0.0;
        protected double heatFluxPerArea = 0.0;

        [KSPField(isPersistant = false, guiActive = false, guiName = "Rec. Heat", guiUnits = "", guiFormat = "F4")]
        protected string displayRecordedHeatLoad = "0 W";
        
        [KSPField(isPersistant = false, guiActive = false, guiName = "Max. Rec. Heat", guiUnits = "", guiFormat = "F4")]
        protected string displayMaximumRecordedHeat = "0 W";

        [KSPField(isPersistant = false, guiActive = false, guiName = "Heat/cm2", guiUnits = "", guiFormat = "F4")]
        protected string displayHeatFluxPerArea = "0 W/cm2";

        [KSPEvent(guiName = "Reset Heat Record", guiActiveUnfocused = true, externalToEVAOnly = false, guiActive = false, unfocusedRange = 4f)]
        public void ResetRecordedHeat()
        {
            displayRecordedHeatLoad = "0 W";
            displayMaximumRecordedHeat = "0 W";
            recordedHeatLoad = 0f;
            maximumRecordedHeat = 0f;
            if ((object)myWindow != null)
                myWindow.displayDirty = true;
        }

        [KSPEvent(guiName = "No Damage", guiActiveUnfocused = true, externalToEVAOnly = false, guiActive = false, unfocusedRange = 4f)]
        public void RepairDamage()
        {
            if(damage > 0)
            {
                int requiredSkill = 0;

                if(damage > 0.75)
                    requiredSkill = 5;
                else if(damage > 0.5)
                    requiredSkill = 4;
                else if(damage > 0.25)
                    requiredSkill = 3;
                else if(damage > 0.125)
                    requiredSkill = 2;
                else if(damage > 0)
                    requiredSkill = 1;

                if(FlightGlobals.ActiveVessel.VesselValues.RepairSkill.value >= requiredSkill)
                {
                    damage = damage - UnityEngine.Random.Range(0.0f, 0.1f);
                    if(damage < 0)
                        damage = 0;

                    ProcessDamage();
                    SetDamageLabel();
                    if((object)myWindow != null)
                        myWindow.displayDirty = true;
                } else
                    ScreenMessages.PostScreenMessage("<color=orange>[DeadlyReentry]: " + this.part.partInfo.title + " is too badly damaged for this Kerbal's skill level.</color>", 6f, ScreenMessageStyle.UPPER_LEFT);
            }
        }

        EventData<GameEvents.ExplosionReaction> ReentryReaction = GameEvents.onPartExplode;
        
        UIPartActionWindow _myWindow = null; 
        UIPartActionWindow myWindow 
        {
            get
            {
                if((object)_myWindow == null)
                {
                    UIPartActionWindow[] windows = FindObjectsOfType<UIPartActionWindow>();
                    for(int i = windows.Length - 1; i >= 0; --i)
                    {
                        if (windows[i].part == part)
                        {
                            _myWindow = windows[i];
                            break;
                        }
                    }
                }
                return _myWindow;
            }
        }
        
        static string FormatTime(double time)
        {
            int iTime = (int) time % 3600;
            int seconds = iTime % 60;
            int minutes = (iTime / 60) % 60;
            int hours = (iTime / 3600);
            return hours.ToString ("D2") 
                + ":" + minutes.ToString ("D2") + ":" + seconds.ToString ("D2");
        }

        static string FormatFlux(double flux, bool scale = false)
        {
            if (scale)
                flux *= TimeWarp.fixedDeltaTime;
            if (flux >= 1000000000.0)
                return (flux / 1000000000.0).ToString("F2") + " T";
            else if (flux >= 1000000.0)
                return (flux / 1000000.0).ToString("F2") + " G";
            else if (flux >= 1000.0)
                return (flux / 1000.0).ToString("F2") + " M";
            else if (flux >= 1.0)
                return (flux).ToString("F2") + " k";
            else
                return (flux * 1000.0).ToString("F2");
            
        }

        public static void PlaySound(FXGroup fx, float volume)
        {
            if(fx.audio.isPlaying)
                {
                if(fx.audio.volume < volume)
                    fx.audio.volume = volume;
            } else {
                fx.audio.volume = volume;
                fx.audio.Play ();
            }
            //if(this.is_debugging)
            //    print (fx.audio.clip.name);
            
        }
        FXGroup _gForceFX = null;
        FXGroup gForceFX 
        {
            get
            {
                if((object)_gForceFX == null)
                {
                    _gForceFX = new FXGroup (part.name + "_Crushing");
                    _gForceFX.audio = gameObject.AddComponent<AudioSource>();
                    _gForceFX.audio.clip = GameDatabase.Instance.GetAudioClip("DeadlyReentry/Sounds/gforce_damage");
                    _gForceFX.audio.spatialBlend = 1f;
                    _gForceFX.audio.volume = GameSettings.SHIP_VOLUME;
                    _gForceFX.audio.Stop ();
                }
                return _gForceFX;
                
            }
        }
        
        FXGroup _ablationSmokeFX = null;
        FXGroup ablationSmokeFX 
        {
            get
            {
                if((object)_ablationSmokeFX == null)
                {
                    _ablationSmokeFX = new FXGroup (part.name + "_Smoking");
                    _ablationSmokeFX.fxEmitters.Add (Emitter("fx_smokeTrail_medium").GetComponent<ParticleEmitter>());
                }
                return _ablationSmokeFX;
            }
        }
        
        FXGroup _ablationFX = null;
        FXGroup ablationFX 
        {
            get
            {
                if((object)_ablationFX == null)
                {
                    _ablationFX = new FXGroup (part.name + "_Burning");
                    _ablationFX.fxEmitters.Add (Emitter("fx_exhaustFlame_yellow").GetComponent<ParticleEmitter>());
                    _ablationFX.fxEmitters.Add(Emitter("fx_exhaustSparks_yellow").GetComponent<ParticleEmitter>());
                    _ablationFX.audio = gameObject.AddComponent<AudioSource>();
                    _ablationFX.audio.clip = GameDatabase.Instance.GetAudioClip("DeadlyReentry/Sounds/fire_damage");
                    _ablationFX.audio.spatialBlend = 1f;
                    _ablationFX.audio.volume = GameSettings.SHIP_VOLUME;
                    _ablationFX.audio.Stop ();
                    
                }
                return _ablationFX;
            }
        }
        
        public override void OnAwake()
        {
            base.OnAwake();
            if (!CompatibilityChecker.IsAllCompatible())
            {
                isCompatible = false;
                return;
            }

            // are we an engine?
            for (int i = part.Modules.Count - 1; i >= 0; --i)
            {
                if (part.Modules[i] is ModuleEngines)
                {
                    is_engine = true;
                    break;
                }
            }

            if (maxOperationalTemp == -1)
                maxOperationalTemp = part.maxTemp * 0.85;
            if (maxSkinOperationalTemp == -1)
                maxSkinOperationalTemp = part.skinMaxTemp * 0.85;
        }

        void OnDestroy()
        {
            //FI = null;
            if((object)_ablationFX != null && (object)_ablationFX.audio != null)
                DestroyImmediate(_ablationFX.audio);
            if((object)_gForceFX != null && (object)_gForceFX.audio != null)
                DestroyImmediate(_gForceFX.audio);
        }

        public virtual void FixedUpdate()
        {
            if (!FlightGlobals.ready)
                return;

            CheckForFire();
            CheckGeeForces();
            if (is_debugging && vessel.mach > 1.0)
            {
                recordedHeatLoad += part.thermalConvectionFlux;
                maximumRecordedHeat = Math.Max(maximumRecordedHeat, part.thermalConvectionFlux);
                heatFluxPerArea = part.thermalConvectionFlux / part.skinExposedArea;

                displayRecordedHeatLoad = FormatFlux(recordedHeatLoad, true) + "J";
                displayMaximumRecordedHeat = FormatFlux(maximumRecordedHeat) + "W";
                //displayMaximumRecordedHeat = part.skinExposedArea.ToString("F2") + " / " + part.radiativeArea.ToString("F2") + "  m2";
;
                displayHeatFluxPerArea = FormatFlux(heatFluxPerArea/10000) + "W/cm2";
            }
        }
        
        public virtual void Update()
        {
            if (is_debugging != PhysicsGlobals.ThermalDataDisplay)
            {
                is_debugging = PhysicsGlobals.ThermalDataDisplay;

                //Fields["FIELD-TO-DISPLAY"].guiActive = PhysicsGlobals.ThermalDataDisplay;
                Fields["displayRecordedHeatLoad"].guiActive = PhysicsGlobals.ThermalDataDisplay;
                Fields["displayMaximumRecordedHeat"].guiActive = PhysicsGlobals.ThermalDataDisplay;
                Fields["displayHeatFluxPerArea"].guiActive = PhysicsGlobals.ThermalDataDisplay;
                Events["ResetRecordedHeat"].guiActive = PhysicsGlobals.ThermalDataDisplay;

                if ((object)myWindow != null)
                {
                    myWindow.displayDirty = true;
                }
            }
        }
        
        public void CheckGeeForces()
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && !CheatOptions.UnbreakableJoints)
            {
                if (dead || (object)vessel == null || TimeWarp.fixedDeltaTime > 0.5 || TimeWarp.fixedDeltaTime <= 0)
                    return; // don't check G-forces in warp
                
                double geeForce = vessel.geeForce_immediate;
                if (geeForce > 40 && geeForce > lastGForce)
                {
                    // G forces over 40 are probably a Kraken twitch unless they last multiple frames
                    displayGForce = displayGForce * (1 - TimeWarp.fixedDeltaTime) + (float)(lastGForce * TimeWarp.fixedDeltaTime);
                }
                else
                {
                    //keep a running average of G force over 1s, to further prevent absurd spikes (mostly decouplers & parachutes)
                    displayGForce = displayGForce * (1 - TimeWarp.fixedDeltaTime) + (float)(geeForce * TimeWarp.fixedDeltaTime);
                }
                if (displayGForce < ReentryPhysics.crewGMin)
                    gExperienced = 0;
                
                //double gTolerance;
                if (gTolerance < 0)
                {
                    if (is_engine && damage < 1)
                        gTolerance = (float)Math.Pow(UnityEngine.Random.Range(11.9f, 12.1f) * part.crashTolerance, 0.5);
                    else
                        gTolerance = (float)Math.Pow(UnityEngine.Random.Range(5.9f, 6.1f) * part.crashTolerance, 0.5);
                    
                    gTolerance *= ReentryPhysics.gToleranceMult;
                }
                if (gTolerance >= 0 && displayGForce > gTolerance)
                { // G tolerance is based roughly on crashTolerance
                    AddDamage(TimeWarp.fixedDeltaTime * (float)(displayGForce / gTolerance - 1));
                    if (!vessel.isEVA)
                    { // kerbal bones shouldn't sound like metal when they break.
                        gForceFX.audio.pitch = (float)(displayGForce / gTolerance);
                        PlaySound(gForceFX, damage * 0.3f + 0.7f);
                        is_gforce_fx_playing = true;
                    }
                }
                else if (is_gforce_fx_playing)
                {
                    double new_volume = (gForceFX.audio.volume *= 0.8f);
                    if (new_volume < 0.001f)
                    {
                        gForceFX.audio.Stop();
                        is_gforce_fx_playing = false;
                    }
                }
                if (damage >= 1.0f && !dead)
                {
                    dead = true;
                    FlightLogger.fetch.LogEvent("[" + FormatTime(vessel.missionTime) + "] "
                                              + part.partInfo.title + " exceeded g-force tolerance.");
                    // TODO See if we still need this or similar code. Rewrite if needed. Remove if obsolete.
                    //if ( part is StrutConnector )
                    //{
                    //    ((StrutConnector)part).BreakJoint();
                    //}
                    
                    part.explode();
                    return;
                }

                float crewGKillChance = 1f-Mathf.Pow(1f-ReentryPhysics.crewGKillChance, TimeWarp.fixedDeltaTime / 0.02f);
                if (Math.Max(displayGForce, geeForce) >= ReentryPhysics.crewGMin)
                {
                    gExperienced += Math.Pow(Math.Min(Math.Abs(Math.Max(displayGForce, geeForce)), ReentryPhysics.crewGClamp), ReentryPhysics.crewGPower) * TimeWarp.fixedDeltaTime;
                    List<ProtoCrewMember> crew = part.protoModuleCrew; //vessel.GetVesselCrew();
                    if (gExperienced > ReentryPhysics.crewGWarn && crew.Count > 0)
                    {
                        if (DeadlyReentryScenario.displayCrewGForceWarning && gExperienced < ReentryPhysics.crewGLimit)
                            ScreenMessages.PostScreenMessage(ReentryPhysics.crewGWarningMsg);
                        else
                        {
                            // borrowed from TAC Life Support
                            if (UnityEngine.Random.Range(0f, 1f) < crewGKillChance)
                            {
                                int crewMemberIndex = UnityEngine.Random.Range(0, crew.Count - 1);
                                if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA)
                                {
                                    CameraManager.Instance.SetCameraFlight();
                                }
                                ProtoCrewMember member = crew[crewMemberIndex];
                                
                                ScreenMessages.PostScreenMessage(vessel.vesselName + ": Crewmember " + member.name + " died of G-force damage!", 30.0f, ScreenMessageStyle.UPPER_CENTER);
                                FlightLogger.fetch.LogEvent("[" + FormatTime(vessel.missionTime) + "] "
                                                          + member.name + " died of G-force damage.");
                                Debug.Log("*DRE* [" + Time.time + "]: " + vessel.vesselName + " - " + member.name + " died of G-force damage.");
                                
                                if (!vessel.isEVA)
                                {
                                    part.RemoveCrewmember(member);
                                    member.Die();
                                }
                            }
                        }
                    }
                }
                lastGForce = vessel.geeForce_immediate;
            }
        }
        
        public void AddDamage(float dmg)
        {
            if (dead || (object)part == null || (object)part.partInfo == null || (object)part.partInfo.partPrefab == null)
                return;
            if(is_debugging)
                print (part.partInfo.title + ": +" + dmg + " damage");
            damage += dmg;
            ProcessDamage();
            SetDamageLabel ();
        }
        
        public void ProcessDamage()
        {
            if (!vessel.isEVA)
            {
                //part.skinMaxTemp = part.partInfo.partPrefab.skinMaxTemp * (1 - 0.15f * damage);
                part.breakingForce = part.partInfo.partPrefab.breakingForce * (1 - damage);
                part.breakingTorque = part.partInfo.partPrefab.breakingTorque * (1 - damage);
                part.crashTolerance = part.partInfo.partPrefab.crashTolerance * (1 - 0.5f * damage);
            }
        }

        public void SetDamageLabel() 
        {
            if(!vessel.isEVA)
            {
                if((object)Events == null)
                    return;
                if(damage > 0.5)
                    Events["RepairDamage"].guiName = "Repair Critical Damage";
                else if(damage > 0.25)
                    Events["RepairDamage"].guiName = "Repair Heavy Damage";
                else if(damage > 0.125)
                    Events["RepairDamage"].guiName = "Repair Moderate Damage";
                else if(damage > 0)
                    Events["RepairDamage"].guiName = "Repair Light Damage";
                else
                    Events["RepairDamage"].guiName = "No Damage";
            }
        }


        public void CheckForFire()
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && !CheatOptions.IgnoreMaxTemperature)
            {
                if (FlightGlobals.ActiveVessel.situation != Vessel.Situations.PRELAUNCH || FlightGlobals.ActiveVessel.missionTime > 2.0)
                {
                    if (dead)
                        return;
                    double damageThreshold;
                    
                    if (is_engine && damage < 1)
                        damageThreshold = part.skinMaxTemp * 0.975;
                    else if (vessel.isEVA)
                    {
                        damageThreshold = 800 * (1 - damage) * (1 - damage);
                        part.skinMaxTemp = 900;
                    }
                    else
                        damageThreshold = part.skinMaxTemp * 0.85;
                    
                    if (part.skinTemperature > damageThreshold)
                    {
                        // Handle client-side fire stuff.
                        // OH GOD IT'S ON FIRE.
                        float tempRatio = (float)((part.skinTemperature / damageThreshold) - 1.0);
                        tempRatio *= (float)((part.skinTemperature / part.skinMaxTemp) * (part.skinTemperature / part.skinMaxTemp) * 4.0);
                        AddDamage(TimeWarp.deltaTime * (float)((damage + 1.0) * tempRatio));
                        float soundTempRatio = (float)(part.skinTemperature / part.skinMaxTemp);
                        PlaySound(ablationFX, soundTempRatio * soundTempRatio);
                        
                        if (is_engine && damage < 1)
                            part.skinTemperature = UnityEngine.Random.Range(0.97f + 0.05f * damage, 0.98f + 0.05f * damage) * part.skinMaxTemp;
                        else if (damage < 1)// non-engines can keep burning
                            part.skinTemperature += UnityEngine.Random.Range(0.5f + 0.5f * damage, 1.0f + 0.5f * damage) * (tempRatio * 0.04f * part.skinMaxTemp * TimeWarp.fixedDeltaTime);
                        
                        if (part.skinTemperature > part.skinMaxTemp || damage >= 1.0f)
                        { // has it burnt up completely?
                            
                            List<ParticleEmitter> fxs = ablationFX.fxEmitters;
                            for(int i = fxs.Count-1; i >= 0; --i)
                                GameObject.DestroyImmediate(fxs[i].gameObject);
                            fxs = ablationSmokeFX.fxEmitters;
                            for(int i = fxs.Count-1; i >= 0; --i)
                                GameObject.DestroyImmediate(fxs[i].gameObject);

                            if (!dead)
                            {
                                dead = true;
                                FlightLogger.fetch.LogEvent("[" + FormatTime(vessel.missionTime) + "] "
                                    + part.partInfo.title + " burned up from overheating.");
                                
                                // TODO See if we still need this or similar code. Rewrite if needed. Remove if obsolete.
                                //if ( part is StrutConnector )
                                //{
                                //    ((StrutConnector)part).BreakJoint();
                                //}
                                
                                part.explode();
                                return;
                            }
                        }
                        else
                        {
                            is_on_fire = true;
                            List<ParticleEmitter> fxs = ablationFX.fxEmitters;
                            ParticleEmitter fx;
                            for (int i = fxs.Count - 1; i >= 0; --i)
                            {
                                fx = fxs[i];
                                fx.gameObject.SetActive(true);
                                fx.gameObject.transform.LookAt(part.transform.position + vessel.srf_velocity);
                                fx.gameObject.transform.Rotate(90, 0, 0);
                            }
                            fxs = ablationSmokeFX.fxEmitters;
                            for (int i = fxs.Count - 1; i >= 0; --i)
                            {
                                fx = fxs[i];
                                fx.gameObject.SetActive(vessel.atmDensity > 0.02);
                                fx.gameObject.transform.LookAt(part.transform.position + vessel.srf_velocity);
                                fx.gameObject.transform.Rotate(90, 0, 0);
                            }
                            float severity = (float)((this.part.skinMaxTemp * 0.85) / this.part.skinMaxTemp);
                            float distance = Vector3.Distance(this.part.partTransform.position, FlightGlobals.ActiveVessel.vesselTransform.position);
                            ReentryReaction.Fire(new GameEvents.ExplosionReaction(distance, severity));
                        }
                    }
                    else if (is_on_fire)
                    { // not on fire.
                        is_on_fire = false;

                        List<ParticleEmitter> fxs = ablationFX.fxEmitters;
                        for (int i = fxs.Count - 1; i >= 0; --i)
                            fxs[i].gameObject.SetActive(false);
                        fxs = ablationSmokeFX.fxEmitters;
                        for (int i = fxs.Count - 1; i >= 0; --i)
                            fxs[i].gameObject.SetActive(false);
                    }
                }
            }
        }
        
        public GameObject Emitter(string fxName)
        {
            GameObject fx = (GameObject)UnityEngine.Object.Instantiate (UnityEngine.Resources.Load ("Effects/" + fxName));
            
            fx.transform.parent = part.transform;
            fx.transform.localPosition = new Vector3 (0, 0, 0);
            fx.transform.localRotation = Quaternion.identity;
            fx.SetActive (false);
            return fx;      
        }

        public override void OnStart(PartModule.StartState state)
        {
            if (!HighLogic.LoadedSceneIsFlight)
                return;
            
            // disable any menu items that might cause trouble on an EVA Kerbal
            if (vessel.isEVA)
                Events["RepairDamage"].guiActive = false;
            else
                ProcessDamage();
        }

        static void print(string msg)
        {
            MonoBehaviour.print("[DeadlyReentry.ModuleAeroReentry] " + msg);
        }
    }

    // TODO WOULD deprecate ModuleHeatShield but still needed for depletedMaxTemp.
    class ModuleHeatShield : ModuleAblator
    {
        public PartResource _ablative = null; // pointer to the PartResource

        [KSPField()]
        protected double depletedMaxTemp = 1200.0;

        [KSPField]
        protected double depletedConductivity = 20.0;

        public new void Start()
        {
            if (!HighLogic.LoadedSceneIsFlight)
                return;
            
            base.Start();
            if((object)ablativeResource != null && ablativeResource != "")
            {
                if(part.Resources.Contains(ablativeResource))
                {
                    _ablative = part.Resources[ablativeResource];
                } else
                    print("ablative lookup failed!");
            }
            else
                print("Heat shield missing ablative! If you are seeing this then a third party mod is using outdated ModuleHeatShield configs!");
        }

        public new void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready)
                return;
            //if (ablativeResource == "")
                // silently fail.
            //    return;
            base.FixedUpdate ();
            if (_ablative.amount <= _ablative.maxAmount * 0.000001)
            {
                part.skinMaxTemp = Math.Min(part.skinMaxTemp, depletedMaxTemp);
                part.heatConductivity = Math.Min(part.heatConductivity, depletedConductivity);
                part.skinSkinConductionMult = Math.Min(part.skinSkinConductionMult, depletedConductivity);
                part.skinInternalConductionMult = Math.Min(part.skinInternalConductionMult, depletedConductivity);
            }
        }
    }
    
    [KSPAddon(KSPAddon.Startup.MainMenu, false)] // fixed
    public class FixMaxTemps : MonoBehaviour
    {
        //public PartModule RFEngineConfig = null;
        //public FieldInfo[] RFEConfigs = null;
        
        public void Start()
        {
            if (!CompatibilityChecker.IsAllCompatible())
                return;
            Debug.Log("FixMaxTemps: Fixing Temps");
            foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes ("REENTRY_EFFECTS"))
            {
                if(node.HasValue("name") && node.GetValue("name") == "Default" && node.HasValue("ridiculousMaxTemp"))
                {
                    double maxTemp;
                    double scale = 0.5f;
                    if(node.HasValue ("maxTempScale"))
                        double.TryParse(node.GetValue("maxTempScale"), out scale);
                    if(scale > 0 && double.TryParse(node.GetValue("ridiculousMaxTemp"), out maxTemp))
                    {
                        Debug.Log("Using ridiculousMaxTemp = " + maxTemp.ToString() + " / maxTempScale =" + scale.ToString());
                        if ((object)PartLoader.LoadedPartsList != null)
                        {
                            foreach (AvailablePart part in PartLoader.LoadedPartsList)
                            {
                                try
                                {
                                    if ((object)part.partPrefab != null && !(part.partPrefab.Modules.Contains("ModuleHeatShield") || part.partPrefab.Modules.Contains("ModuleAblator")))
                                    {
                                        if (part.partPrefab.Modules.Contains("ModuleAeroReentry"))
                                        {
                                            if (((ModuleAeroReentry)(part.partPrefab.Modules["ModuleAeroReentry"])).leaveTemp)
                                            {
                                                Debug.Log("[DRE] skipping part " + part.name + " (leaveTemp = True)");
                                                continue;
                                            }
                                        }
                                        if (part.name == "flag")
                                        {
                                            Debug.Log("[DRE] Ignoring part 'flag'");
                                            continue;
                                        }
                                        double oldTemp = part.partPrefab.maxTemp;
                                        bool changed = false;
                                        if (part.partPrefab.maxTemp > maxTemp)
                                        {
                                            part.partPrefab.maxTemp = Math.Min(part.partPrefab.maxTemp * scale, maxTemp);
                                            Debug.Log("[DRE] rebalancing OP maxTemp for part " + part.name);
                                            changed = true;
                                        }
                                        if (changed)
                                        {
                                            double curScale = part.partPrefab.maxTemp / oldTemp;

                                            foreach (PartModule module in part.partPrefab.Modules)
                                            {
                                                if (module is ModuleEngines)
                                                    ((ModuleEngines)module).heatProduction *= (float)curScale;
                                            }
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    print("Error processing part maxTemp " + part.name);
                                    Debug.Log(e.Message);
                                }
                                try
                                {
                                    if ((object)part.partPrefab != null && (object)part.partPrefab.Modules != null)
                                    {
                                        bool add = true;
                                        for (int i = part.partPrefab.Modules.Count - 1; i >= 0; --i)
                                        {
                                            if (part.partPrefab.Modules[i] is ModuleAeroReentry)
                                            {
                                                add = false;
                                                break;
                                            }
                                            if (part.name == "flag")
                                            {
                                                Debug.Log("Not adding ModuleAeroReentry to part 'flag'");
                                                add = false;
                                                break;
                                            }
                                        }
                                        if (add)
                                            part.partPrefab.AddModule("ModuleAeroReentry");
                                    }
                                }
                                catch (Exception e)
                                {
                                    print("Error adding ModuleAeroReentry to " + part.name);
                                    Debug.Log(e.Message);

                                }
                            }
                        }
                    }
                }
            }
        }
    }
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class ReentryPhysics : MonoBehaviour
    {
        private static AerodynamicsFX _afx;
        
        public static AerodynamicsFX afx {
            get
            {
                if ((object)_afx == null)
                {
                    GameObject fx = GameObject.Find ("FXLogic");
                    if ((object)fx != null)
                {
                        _afx = fx.GetComponent<AerodynamicsFX> ();
                    }
                }
                return _afx;
            }
        }
        
        public static Vector3 frameVelocity;
        
        public static GUIStyle warningMessageStyle = new GUIStyle();
        public static FontStyle fontStyle = new FontStyle();
        
        public static ScreenMessage crewGWarningMsg = new ScreenMessage("<color=#ff0000>Reaching Crew G limit!</color>", 1f, ScreenMessageStyle.UPPER_CENTER);

        public static float gToleranceMult = 6.0f;
        public static float crewGClamp = 30;
        public static float crewGPower = 4;
        public static float crewGMin = 5;
        public static float crewGWarn = 300000;
        public static float crewGLimit = 600000;
        public static float crewGKillChance = 0.75f;


        public static bool debugging = false;
        private static bool isCompatible = true;

        public void Start()
        {
            if (!CompatibilityChecker.IsAllCompatible())
            {
                return;
            }
            enabled = true; // 0.24 compatibility
            Debug.Log("[DRE] - ReentryPhysics.Start(): LoadSettings(), Difficulty: " + DeadlyReentryScenario.DifficultyName);
            LoadSettings(); // Moved loading of REENTRY_EFFECTS into a generic loader which uses new difficulty settings
            //warningMessageStyle.font = GUI.skin.font;
            //warningMessageStyle.fontSize = 32;
            //warningMessageStyle.
            //warningMessageStyle.fontStyle = GUI.skin.label.fontStyle;
            //crewGWarningMsg.guiStyleOverride = warningMessageStyle;
        }
        public static void LoadSettings()
        {
            if (!CompatibilityChecker.IsAllCompatible())
            {
                isCompatible = false;
                return;
            }
            foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes ("REENTRY_EFFECTS"))
            {
                if (node.HasValue("name") && node.GetValue("name") == DeadlyReentryScenario.DifficultyName)
                {
                    if (node.HasValue("gToleranceMult"))
                        float.TryParse(node.GetValue("gToleranceMult"), out gToleranceMult);                    
                    if (node.HasValue("crewGClamp"))
                        float.TryParse(node.GetValue("crewGClamp"), out crewGClamp);
                    if (node.HasValue("crewGPower"))
                        float.TryParse(node.GetValue("crewGPower"), out crewGPower);
                    if (node.HasValue("crewGMin"))
                        float.TryParse(node.GetValue("crewGMin"), out crewGMin);
                    if (node.HasValue("crewGWarn"))
                        float.TryParse(node.GetValue("crewGWarn"), out crewGWarn);
                    if (node.HasValue("crewGLimit"))
                        float.TryParse(node.GetValue("crewGLimit"), out crewGLimit);
                    if (node.HasValue("crewGKillChance"))
                        float.TryParse(node.GetValue("crewGKillChance"), out crewGKillChance);
                    
                    break;
                }
            }
        }
        
        public static void SaveSettings()
        {
            if (!CompatibilityChecker.IsAllCompatible())
            {
                isCompatible = false;
                return;
            }
            foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes ("REENTRY_EFFECTS"))
            {
                if (node.HasValue("name") && node.GetValue("name") == DeadlyReentryScenario.DifficultyName)
                {
                    if (node.HasValue("gToleranceMult"))
                        node.SetValue("gToleranceMult", gToleranceMult.ToString());                  
                    if (node.HasValue("crewGClamp"))
                        node.SetValue("crewGClamp", crewGClamp.ToString());
                    if (node.HasValue("crewGPower"))
                        node.SetValue("crewGPower", crewGPower.ToString());
                    if (node.HasValue("crewGMin"))
                        node.SetValue("crewGMin", crewGMin.ToString());
                    if (node.HasValue("crewGWarn"))
                        node.SetValue("crewGWarn", crewGWarn.ToString());
                    if (node.HasValue("crewGLimit"))
                        node.SetValue("crewGLimit", crewGLimit.ToString());
                    if (node.HasValue("crewGKillChance"))
                        node.SetValue("crewGKillChance", crewGKillChance.ToString());
                    
                    if(node.HasValue("debugging"))
                        node.SetValue("debugging", debugging.ToString());
                    break;
                }
            }
            SaveCustomSettings();
        }
        public static void SaveCustomSettings()
        {
            if (!CompatibilityChecker.IsAllCompatible())
            {
                isCompatible = false;
                return;
            }
            string[] difficultyNames = {"Default"};
            float ftmp;
            double dtmp;
            
            ConfigNode savenode = new ConfigNode();
            foreach(string difficulty in difficultyNames)
            {
                foreach (ConfigNode settingNode in GameDatabase.Instance.GetConfigNodes ("REENTRY_EFFECTS"))
                {
                    if (settingNode.HasValue("name") && settingNode.GetValue("name") == difficulty)
                    {
                        // This is :Final because it represents player choices and must not be overridden by other mods.
                        ConfigNode node = new ConfigNode("@REENTRY_EFFECTS[" + difficulty + "]:Final");
                        
                        if (settingNode.HasValue("gToleranceMult"))
                        {
                            float.TryParse(settingNode.GetValue("gToleranceMult"), out ftmp);
                            node.AddValue ("@gToleranceMult", ftmp);
                        }

                        if (settingNode.HasValue("crewGKillChance"))
                        {
                            float.TryParse(settingNode.GetValue("crewGKillChance"), out ftmp);
                            node.AddValue ("@crewGKillChance", ftmp);
                        }                        
                        
                        if (settingNode.HasValue("crewGClamp"))
                        {
                            double.TryParse(settingNode.GetValue("crewGClamp"), out dtmp);
                            node.AddValue ("@crewGClamp", dtmp);
                        }

                        if (settingNode.HasValue("crewGPower"))
                        {
                            double.TryParse(settingNode.GetValue("crewGPower"), out dtmp);
                            node.AddValue ("@crewGPower", dtmp);
                        }

                        if (settingNode.HasValue("crewGMin"))
                        {
                            double.TryParse(settingNode.GetValue("crewGMin"), out dtmp);
                            node.AddValue ("@crewGMin", dtmp);
                        }

                        if (settingNode.HasValue("crewGWarn"))
                        {
                            double.TryParse(settingNode.GetValue("crewGWarn"), out dtmp);
                            node.AddValue ("@crewGWarn", dtmp);
                        }

                        if (settingNode.HasValue("crewGLimit"))
                        {
                            double.TryParse(settingNode.GetValue("crewGLimit"), out dtmp);
                            node.AddValue ("@crewGLimit", dtmp);
                        }
                        
                        savenode.AddNode (node);
                        break;
                    }
                }
            }
            savenode.Save (KSPUtil.ApplicationRootPath.Replace ("\\", "/") + "GameData/DeadlyReentry/custom.cfg");
        }
    }
}