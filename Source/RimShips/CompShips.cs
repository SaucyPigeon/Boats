﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Harmony;
using RimWorld;
using RimWorld.BaseGen;
using RimWorld.Planet;
using RimShips.Build;
using RimShips.Defs;
using UnityEngine;
using UnityEngine.AI;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Verse.Sound;

namespace RimShips
{
    public enum ShipWeaponStatus { Offline, Online }
    public enum ShipMovementStatus { Offline, Online }
    public class CompShips : ThingComp
    {
        public List<Jobs.Bill_BoardShip> bills = new List<Jobs.Bill_BoardShip>();

        public bool draftStatusChanged = false;
        public bool beached = false;
        private float angle = 0f;
        
        public List<ShipHandler> handlers = new List<ShipHandler>();
        public Rot4 lastDirection = Rot4.South;
        public ShipMovementStatus movementStatus = ShipMovementStatus.Online;
        public List<ThingCountClass> repairCostList = new List<ThingCountClass>();

        public bool warnNoFuel;
        public ShipWeaponStatus weaponStatus = ShipWeaponStatus.Offline;

        public bool CanMove => Props.moveable > ShipPermissions.DriverNeeded || MovementHandlerAvailable;

        public Pawn Pawn => parent as Pawn;
        public ShipProperties Props => (ShipProperties)props;

        public bool MovementHandlerAvailable
        {
            get
            {
                bool result = true;
                if (!(handlers is null) && handlers.Any())
                {
                    foreach(ShipHandler handler in handlers)
                    {
                        if(handler.handlers.Count < handler.role.slotsToOperate && handler.role.handlingTypes is HandlingTypeFlags.Movement)
                        {
                            result = false;
                            break;
                        }
                    }
                }
                if (!(this.Pawn.TryGetComp<CompRefuelable>() is null) && this.Pawn.GetComp<CompRefuelable>().Fuel <= 0f)
                    result = false;
                return result;
            }
        }

        public int PawnCountToOperate
        {
            get
            {
                int pawnCount = 0;
                foreach(ShipRole r in Props.roles)
                {
                    pawnCount += r.slotsToOperate;
                    foreach(ShipHandler handler in handlers)
                    {
                        if(handler.role.slotsToOperate > 0)
                        {
                            pawnCount -= handler.handlers.Count;
                        }
                    }
                }
                return pawnCount >= 0 ? pawnCount : 0;
            }
        }

        public List<Pawn> AllPawnsAboard
        {
            get
            {
                List<Pawn> pawnsOnShip = new List<Pawn>();
                if (!(handlers is null) && handlers.Count > 0)
                {
                    foreach (ShipHandler handler in handlers)
                    {
                        if (!(handler.handlers is null) && handler.handlers.Count > 0) pawnsOnShip.AddRange(handler.handlers);
                    }
                }
                
                return pawnsOnShip;
            }
        }

        public int SeatsAvailable
        {
            get
            {
                int x = 0;
                foreach(ShipHandler handler in handlers)
                {
                    x += handler.role.slots - handler.handlers.Count;
                }
                return x;
            }
        }

        public void Rename()
        {
            if(this.Props.nameable)
            {
                Find.WindowStack.Add(new Dialog_GiveShipName(this.Pawn));
            }
        }
        public float Angle
        {
            get
            {
                return this.angle;
            }
            set
            {
                if (value == this.angle)
                {
                    return;
                }
                this.angle = value;
            }
        }
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!this.Pawn.Dead && !this.Pawn.Drafted)
            {
                Command_Action unloadAll = new Command_Action();
                unloadAll.defaultLabel = "Unload Everyone";
                unloadAll.icon = TexCommandShips.UnloadAll;
                unloadAll.action = delegate ()
                {
                    DisembarkAll();
                };
                unloadAll.hotKey = KeyBindingDefOf.Misc2;
                yield return unloadAll;

                foreach(ShipHandler handler in handlers)
                {
                    for(int i = 0; i < handler.handlers.Count; i++)
                    {
                        Pawn currentPawn = handler.handlers.InnerListForReading[i];
                        Command_Action unload = new Command_Action();
                        unload.defaultLabel = "Unload " + currentPawn.LabelShort;
                        unload.icon = TexCommandShips.UnloadPassenger;
                        unload.action = delegate ()
                        {
                            DisembarkPawn(currentPawn);
                        };
                        yield return unload;
                    }
                }
            }
            yield break;
        }

        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn pawn)
        {
            if (!pawn.RaceProps.ToolUser)
            {
                yield break;
            }
            if (!pawn.CanReserveAndReach(this.parent, PathEndMode.InteractionCell, Danger.Deadly, 1, -1, null, false))
            {
                yield break;
            }
            if (this.movementStatus is ShipMovementStatus.Offline)
            {
                yield break;
            }
            foreach (ShipHandler handler in handlers)
            {
                if (handler.AreSlotsAvailable)
                {
                    FloatMenuOption opt = new FloatMenuOption("BoardShip".Translate(this.parent.LabelShort, handler.role.label, (handler.role.slots - (handler.handlers.Count + handler.currentlyReserving.Count)).ToString()), 
                    delegate ()
                    {
                        Job job = new Job(JobDefOf_Ships.Board, this.parent);
                        pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                        GiveLoadJob(pawn, handler);
                        ReserveSeat(pawn, handler);
                    }, MenuOptionPriority.Default, null, null, 0f, null, null);
                    yield return opt;
                }
            }
            yield break;
        }

        public void GiveLoadJob(Pawn pawn, ShipHandler handler)
        {
            if (this.bills is null) this.bills = new List<Jobs.Bill_BoardShip>();

            if (!(bills is null) && bills.Count > 0)
            {
                Jobs.Bill_BoardShip bill = bills.FirstOrDefault(x => x.pawnToBoard == pawn);
                if (!(bill is null))
                {
                    bill.handler = handler;
                    return;
                }
            }
            bills.Add(new Jobs.Bill_BoardShip(pawn, handler));
        }

        public void Notify_BoardedCaravan(Pawn pawnToBoard, ThingOwner handler)
        {
            if (!pawnToBoard.IsWorldPawn())
            {
                Log.Warning("Tried boarding Caravan with non-worldpawn");
            }

            if (!(pawnToBoard.holdingOwner is null))
            {
                pawnToBoard.holdingOwner.TryTransferToContainer(pawnToBoard, handler);
            }
            else
            {
                handler.TryAdd(pawnToBoard);
            }
        }

        public void Notify_Boarded(Pawn pawnToBoard)
        {
            if( !(bills is null) && (bills.Count > 0))
            {
                Jobs.Bill_BoardShip bill = bills.FirstOrDefault(x => x.pawnToBoard == pawnToBoard);
                if(!(bill is null))
                {
                    if (pawnToBoard.IsWorldPawn())
                    {
                        Log.Warning("Tried boarding ship with world pawn");
                    }

                    Faction faction = pawnToBoard.Faction;
                    pawnToBoard.DeSpawn();
                    
                    if ( !(pawnToBoard.holdingOwner is null))
                    {
                        pawnToBoard.holdingOwner.TryTransferToContainer(pawnToBoard, bill.handler.handlers);
                    }
                    else
                    {
                        bill.handler.handlers.TryAdd(pawnToBoard);
                    }
                    if (!pawnToBoard.IsWorldPawn())
                    {
                        Find.WorldPawns.PassToWorld(pawnToBoard, PawnDiscardDecideMode.Decide);
                    }
                    pawnToBoard.SetFaction(faction);
                    bills.Remove(bill);
                }
            }
        }

        public void DisembarkPawn(Pawn pawn)
        {
            if(!pawn.Spawned)
            {
                GenSpawn.Spawn(pawn, Pawn.PositionHeld.RandomAdjacentCellCardinal(), Pawn.MapHeld);
                if(!(this.Pawn.GetLord() is null))
                {
                    this.Pawn.GetLord().AddPawn(pawn);
                }
            }
            RemovePawn(pawn);
        }

        public void DisembarkAll()
        {
            var pawnsToDisembark = new List<Pawn>(AllPawnsAboard);
            if( !(pawnsToDisembark is null) && pawnsToDisembark.Count > 0)
            {
                if(Pawn.GetCaravan() != null && !Pawn.Spawned)
                {
                    List<ShipHandler> handlerList = this.handlers;
                    for(int i = 0; i < handlerList.Count; i++)
                    {
                        ShipHandler handler = handlerList[i];
                        handler.handlers.TryTransferAllToContainer(Pawn.GetCaravan().pawns, false);
                    }
                    return;
                }
                foreach(Pawn p in pawnsToDisembark)
                {
                    DisembarkPawn(p);
                }
            }
        }
        public void RemovePawn(Pawn pawn)
        {
            if(handlers is List<ShipHandler> handl && !handl.NullOrEmpty())
            {
                List<ShipHandler> tempHandler = handl.FindAll(x => x.handlers.InnerListForReading.Contains(pawn));
                if(!tempHandler.NullOrEmpty())
                {
                    foreach(ShipHandler h in tempHandler)
                    {
                        if (h.handlers.InnerListForReading.Remove(pawn)) return;
                    }
                }
            }
        }

        public void DeadPawnReplace(Pawn pawn)
        {
            //NEEDS IMPLEMENTATION
            /*foreach(ShipHandler h in handlers)
            {
                if(h.handlers.InnerListForReading.Contains(pawn))
                {
                    
                }
            }*/
        }

        public void BeachShip()
        {
            this.movementStatus = ShipMovementStatus.Offline;
            this.beached = true;
        }

        private void TrySatisfyPawnNeeds()
        {
            if(this.Pawn.Spawned)
            {
                foreach (Pawn p in this.AllPawnsAboard)
                {
                    TrySatisfyPawnNeeds(p);
                }
            }
        }
        private void TrySatisfyPawnNeeds(Pawn pawn)
        {
            if (pawn.Dead) return;

            List<Need> allNeeds = pawn.needs.AllNeeds;
            foreach(Need need in allNeeds)
            {
                if(need is Need_Rest)
                {
                    this.TickNeeds(need);
                    this.TrySatisfyRest(pawn, need as Need_Rest);
                }
                else if(need is Need_Food)
                {
                    this.TickNeeds(need);
                    this.TrySatisfyFood(pawn, need as Need_Food);
                }
                else if(need is Need_Chemical)
                {
                    this.TickNeeds(need);
                    this.TrySatisfyChemicalNeed(pawn, need as Need_Chemical);
                }
                else if(need is Need_Joy)
                {
                    this.TickNeeds(need);
                    this.TrySatisfyJoyNeed(pawn, need as Need_Joy);
                }
            }
        }

        private void TrySatisfyRest(Pawn pawn, Need_Rest rest)
        {
            //Add Check
            Building_Bed bed = (Building_Bed)this.Pawn.inventory.innerContainer.InnerListForReading.Find(x => x is Building_Bed); // Reserve?
            float restValue = bed is null ? 0.75f : bed.GetStatValue(StatDefOf.BedRestEffectiveness, true);
            rest.TickResting(restValue);
        }

        private void TrySatisfyFood(Pawn pawn, Need_Food food)
        {
            if (food.CurCategory < HungerCategory.Hungry)
                return;
            Thing thing;
            if(this.TryGetBestFood(pawn, out thing))
            {
                food.CurLevel += thing.Ingested(pawn, food.NutritionWanted);
                if(thing.Destroyed)
                {
                    this.Pawn.inventory.innerContainer.Remove(thing);
                }
                if(!TryGetBestFood(pawn, out thing))
                {
                    Messages.Message("ShipOutOfFood".Translate(this.Pawn.LabelShort), this.Pawn, MessageTypeDefOf.NegativeEvent, false);
                }
            }
        }
        public bool TryGetBestFood(Pawn forPawn, out Thing food)
        {
            List<Thing> list = this.Pawn.inventory.innerContainer.InnerListForReading;
            Thing thing = null;
            float num = 0f;
            foreach(Thing foodItem in list)
            {
                if(CanEatForNutrition(foodItem, forPawn))
                {
                    float foodScore = CaravanPawnsNeedsUtility.GetFoodScore(foodItem, forPawn);
                    if(thing is null || foodScore > num)
                    {
                        thing = foodItem;
                        num = foodScore;
                    }
                }
            }
            if(!(thing is null))
            {
                food = thing;
                return true;
            }
            food = null;
            return false;
        }

        private void TrySatisfyChemicalNeed(Pawn pawn, Need_Chemical chemical)
        {
            if (chemical.CurCategory >= DrugDesireCategory.Satisfied)
                return;
            Thing drug;
            if(TryGetDrugToSatisfyNeed(pawn, chemical, out drug))
                this.IngestDrug(pawn, drug);
        }

        public void IngestDrug(Pawn pawn, Thing drug)
        {
            float num = drug.Ingested(pawn, 0f);
            Need_Food food = pawn.needs.food;
            if(!(food is null))
            {
                food.CurLevel += num;
            }
            if(drug.Destroyed)
            {
                this.Pawn.inventory.innerContainer.Remove(drug);
            }
        }
        public bool TryGetDrugToSatisfyNeed(Pawn forPawn, Need_Chemical chemical, out Thing drug)
        {
            Hediff_Addiction addictionHediff = chemical.AddictionHediff;
            if(addictionHediff is null)
            {
                drug = null;
                return false;
            }
            List<Thing> list = this.Pawn.inventory.innerContainer.InnerListForReading;
            Thing thing = null;
            foreach(Thing t in list)
            {
                if(t.IngestibleNow && t.def.IsDrug)
                {
                    CompDrug compDrug = t.TryGetComp<CompDrug>();
                    if(!(compDrug is null) && !(compDrug.Props.chemical is null))
                    {
                        if(compDrug.Props.chemical.addictionHediff == addictionHediff.def)
                        {
                            if(forPawn.drugs is null || forPawn.drugs.CurrentPolicy[t.def].allowedForAddiction || forPawn.story is null || forPawn.story.traits.DegreeOfTrait(TraitDefOf.DrugDesire) > 0)
                            {
                                thing = t;
                                break;
                            }
                        }
                    }
                }
            }
            if(!(thing is null))
            {
                drug = thing;
                return true;
            }
            drug = null;
            return false;
        }
        public static bool CanEatForNutrition(Thing item, Pawn forPawn)
        {
            return item.IngestibleNow && item.def.IsNutritionGivingIngestible && forPawn.WillEat(item, null) && item.def.ingestible.preferability > FoodPreferability.NeverForNutrition &&
                (!item.def.IsDrug || !forPawn.IsTeetotaler()) && (!forPawn.RaceProps.Humanlike || forPawn.needs.food.CurCategory >= HungerCategory.Starving || item.def.ingestible.preferability >
                FoodPreferability.DesperateOnlyForHumanlikes);
        }

        private void TrySatisfyJoyNeed(Pawn pawn, Need_Joy joy)
        {
            if(pawn.IsHashIntervalTick(1250))
            {
                float num = this.Pawn.pather.MovingNow ? 4E-05f : 4E-3f; //Incorporate 'shifts'
                if (num <= 0f)
                    return;
                num *= 1250f;
                List<JoyKindDef> tmpJoyList = GetAvailableJoyKindsFor(pawn);
                JoyKindDef joyKind;
                if (!tmpJoyList.TryRandomElementByWeight((JoyKindDef x) => 1f - Mathf.Clamp01(pawn.needs.joy.tolerances[x]), out joyKind))
                    return;
                joy.GainJoy(num, joyKind);
                tmpJoyList.Clear();
            }
        }

        public List<JoyKindDef> GetAvailableJoyKindsFor(Pawn p)
        {
            List<JoyKindDef> outJoyKinds = new List<JoyKindDef>();
            if (!p.needs.joy.tolerances.BoredOf(JoyKindDefOf.Meditative))
                outJoyKinds.Add(JoyKindDefOf.Meditative);
            if(!p.needs.joy.tolerances.BoredOf(JoyKindDefOf.Social))
            {
                int num = 0;
                foreach(Pawn targetpawn in this.AllPawnsAboard)
                {
                    if(!targetpawn.Downed && targetpawn.RaceProps.Humanlike && !targetpawn.InMentalState)
                    {
                        num++;
                    }
                }
                if (num >= 2)
                    outJoyKinds.Add(JoyKindDefOf.Social);
            }
            return outJoyKinds;
        }

        public void TickNeeds(Need n)
        {
            if(this.Pawn.pather.Moving)
            {
                switch (this.Props.shipPowerType)
                {
                    case ShipType.Paddles:
                        if (n is Need_Rest)
                            n.CurLevel -= 2.15E-05f;
                        else if (n is Need_Food)
                            n.CurLevel -= 2.25E-05f;
                        else if (n is Need_Chemical)
                            n.CurLevel -= 2.0E-05f;
                        else if (n is Need_Joy)
                            n.CurLevel -= 2.0E-05f;
                        break;
                    case ShipType.Sails:
                        if (n is Need_Rest)
                            n.CurLevel -= 2.10E-05f;
                        else if (n is Need_Food)
                            n.CurLevel -= 2.05E-05f;
                        else if (n is Need_Chemical)
                            n.CurLevel -= 2.0E-05f;
                        else if (n is Need_Joy)
                            n.CurLevel -= 2.0E-05f;
                        break;
                    case ShipType.Steam:
                        if (n is Need_Rest)
                            n.CurLevel -= 2.05E-05f;
                        else if (n is Need_Food)
                            n.CurLevel -= 2.0E-05f;
                        else if (n is Need_Chemical)
                            n.CurLevel -= 2.0E-05f;
                        else if (n is Need_Joy)
                            n.CurLevel -= 2.0E-05f;
                        break;
                    case ShipType.Fuel:
                        if (n is Need_Rest)
                            n.CurLevel -= 2.0E-05f;
                        else if (n is Need_Food)
                            n.CurLevel -= 1.8E-05f;
                        else if (n is Need_Chemical)
                            n.CurLevel -= 2.0E-05f;
                        else if (n is Need_Joy)
                            n.CurLevel -= 2.0E-05f;
                        break;
                    case ShipType.Nuclear:
                        if (n is Need_Rest)
                            n.CurLevel -= 2.0E-05f;
                        else if (n is Need_Food)
                            n.CurLevel -= 1.8E-05f;
                        else if (n is Need_Chemical)
                            n.CurLevel -= 2.0E-05f;
                        else if (n is Need_Joy)
                            n.CurLevel -= 2.0E-05f;
                        break;
                    default:
                        throw new NotImplementedException();
                } 
            }
            else
            {
                n.CurLevel -= 2.0E-05f;
            }
        }

        public void ReserveSeat(Pawn p, ShipHandler handler)
        {
            handler.currentlyReserving.Add(p);
        }

        public override void CompTick()
        {
            base.CompTick();
            this.InitializeShip();
            this.TrySatisfyPawnNeeds();
            foreach(ShipHandler handler in handlers)
            {
                handler.ReservationHandler();
            }
        }

        public void InitializeShip()
        {
            if (!(handlers is null) && handlers.Count > 0) return;
            foreach (ShipHandler handler in handlers)
            {
                if(handler.currentlyReserving is null) handler.currentlyReserving = new List<Pawn>();
            }
            if (!(Props.roles is null) && Props.roles.Count > 0)
            {
                foreach (ShipRole role in Props.roles)
                {
                    handlers.Add(new ShipHandler(Pawn, role, new List<Pawn>()));
                }
            }
        }
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            //init your variables here
        }
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref weaponStatus, "weaponStatus", ShipWeaponStatus.Online);
            Scribe_Values.Look(ref movementStatus, "movingStatus", ShipMovementStatus.Online);
            Scribe_Values.Look(ref lastDirection, "lastDirection", Rot4.South);

            Scribe_Collections.Look(ref handlers, "handlers", LookMode.Deep);
            Scribe_Collections.Look(ref bills, "bills", LookMode.Deep);
        }
    }
}