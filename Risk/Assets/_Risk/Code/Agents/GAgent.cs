using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(AnimationController))]
public class GAgent : MonoBehaviour
{
    [Header("Sensors")]
    [SerializeField] Sensor followSensor;
    [SerializeField] Sensor interactionSensor;

    NavMeshAgent navMeshAgent;
    AnimationController animationController;
    Rigidbody rb;
    AgentStats agentStats;
    AgentMemory agentMemory;

    GameObject target;
    Vector3 destination;
    InteractionTarget interactionTarget;

    GAgentGoal lastGoal; 
    GAgentGoal _currentGoal;
    ActionPlan actionPlan;
    GAgentAction _currentAction;

    Dictionary<string, GAgentBelief> beliefs;
    HashSet<GAgentGoal> goals;
    HashSet<GAgentAction> actions;
    IGPlanner goapPlanner;

    void Awake() {
        agentMemory = GetComponent<AgentMemory>();
        navMeshAgent = GetComponent<NavMeshAgent>();
        animationController = GetComponent<AnimationController>();
        agentStats = GetComponent<AgentStats>();
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;

        goapPlanner = new GPlanner();
        followSensor.TargetsChanged += OnTargetsChanged;
        interactionSensor.TargetsChanged += OnTargetsChanged;
    }

    void Start() {
        SetupBeliefs();
        SetupActions();
        SetupGoals();
        PlayerInteraction.instance.SelectionChanged += OnPlayerSelectionChanged;
        PlayerInteraction.instance.SelectionMarkerChanged += OnPlayerSelectionMarkerChanged;
    }

    void SetupBeliefs() {
        beliefs = new Dictionary<string, GAgentBelief>();
        var factory = new BeliefFactory(this, beliefs);

        factory.AddBelief("Nothing", () => false);
        factory.AddBelief("Idle", () => !navMeshAgent.hasPath);
        factory.AddBelief("Moving", () => navMeshAgent.hasPath);

        factory.AddBelief("IsDying", () => agentStats.Health < 30);
        factory.AddBelief("IsHealthy", () => agentStats.Health >= 50);

        factory.AddBelief("IsExhausted", () => agentStats.Stamina < 20);
        factory.AddBelief("IsRested", () => agentStats.Stamina >= 30);

        factory.AddBelief("IsEager", () => agentStats.Relax >= 40);
        factory.AddBelief("IsReluctant", () => agentStats.Relax < 40);

        factory.AddBelief("HasOre", () => agentStats.Ore > 0);
        factory.AddBelief("HasFullOre", () => agentStats.Ore >= agentStats.MaxOre);

        factory.AddBelief("DepositInFollowRange", () => followSensor.TryGetFreeDeposit(agentStats.ID, out _));
        factory.AddBelief("DepositInInteractionRange", () => interactionSensor.TryGetFreeDeposit(agentStats.ID, out _));

        factory.AddBelief("RemembersRest", () => agentMemory.ContainsTargetOfType(BeaconType.REST));
        factory.AddBelief("EnoughBoldness", () => agentStats.Boldness > 50);
        factory.AddBelief("RestInFollowRange", () => followSensor.ContainsTargetOfType(BeaconType.REST));
        factory.AddBelief("RestInInteractionRange", () => interactionSensor.ContainsTargetOfType(BeaconType.REST));
        
        factory.AddBelief("HealInFollowRange", () => followSensor.ContainsTargetOfType(BeaconType.HEAL));
        factory.AddBelief("HealInInteractionRange", () => interactionSensor.ContainsTargetOfType(BeaconType.HEAL));
        
        factory.AddBelief("BuildingInFollowRange", () => followSensor.TryGetAvailableBuilding(agentStats.TeamId, out _));
        factory.AddBelief("BuildingInInteractionRange", () => interactionSensor.TryGetAvailableBuilding(agentStats.TeamId, out _));
        factory.AddBelief("BuildingIsComplete", () => interactionSensor.TryGetAvailableBuilding(agentStats.TeamId, out var building) && building.IsComplete);

        factory.AddBelief("MarkerExists", () => PlayerInteraction.instance.GetMarkerPosition() != Vector3.zero && PlayerInteraction.instance.SelectedAgent == gameObject);
        factory.AddBelief("MarkerInInteractionRange", () => interactionSensor.ContainsTargetOfType(BeaconType.MARKER));
        factory.AddBelief("IsWaitingForOrders", () => false);

        factory.AddBelief("EnemyInFollowRange", () => followSensor.TryGetEnemyStats(agentStats.TeamId, out _));
        factory.AddBelief("EnemyInInteractionRange", () => interactionSensor.TryGetEnemyStats(agentStats.TeamId, out _));
        factory.AddBelief("NoEnemyInRange", () => !followSensor.TryGetEnemyStats(agentStats.TeamId, out _) && !interactionSensor.TryGetEnemyStats(agentStats.TeamId, out _));
    }

    void SetupActions() {
        actions = new HashSet<GAgentAction>();

        // - - - - - RELAX - - - - -

        actions.Add(new GAgentAction.Builder("Relax")
            .WithStrategy(new RelaxStrategy(6, animationController, agentStats))
            .AddEffect(beliefs["Nothing"])
            .Build());

        actions.Add(new GAgentAction.Builder("WanderAround")
            .WithStrategy(new WanderStrategy(navMeshAgent, 10, animationController))
            .AddEffect(beliefs["Moving"])
            .Build());

        // - - - - - HEALING - - - - -

        actions.Add(new GAgentAction.Builder("SearchForHeal")
            .WithStrategy(new WanderStrategy(navMeshAgent, 10, animationController))
            .AddEffect(beliefs["HealInFollowRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("MoveToHealingPosition")
            .WithStrategy(new FollowStrategy(navMeshAgent, () => followSensor.GetNearestTarget(BeaconType.HEAL).transform.position, animationController))
            .AddPrecondition(beliefs["HealInFollowRange"])
            .AddEffect(beliefs["HealInInteractionRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("Heal")
            .WithStrategy(new HealStrategy(3, agentStats, animationController))
            .AddPrecondition(beliefs["HealInInteractionRange"])
            .AddEffect(beliefs["IsHealthy"])
            .Build());

        // - - - - - RESTING - - - - -

        actions.Add(new GAgentAction.Builder("TravelToRest")
            .WithStrategy(new FollowStrategy(navMeshAgent, () => agentMemory.GetNearestTarget(BeaconType.REST).transform.position, animationController))
            .AddPrecondition(beliefs["RemembersRest"])
            // .WithCost(agentStats.CalculateTravelCost())
            .WithCost(10)
            .AddEffect(beliefs["RestInFollowRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("SearchForRest")
            .WithStrategy(new WanderStrategy(navMeshAgent, 10, animationController))
            // .WithCost(agentStats.SearchCost)
            .AddPrecondition(beliefs["EnoughBoldness"])
            .AddEffect(beliefs["RestInFollowRange"])
            .WithCost(5)
            .Build());

        // TODO: Consider saving the target in the InteractionTarget object when the target is first time spotted. In this point Target in follow sensor can be not the same as in belief.
        actions.Add(new GAgentAction.Builder("MoveToRestingPosition")
            .WithStrategy(new FollowStrategy(navMeshAgent, () => followSensor.GetNearestTarget(BeaconType.REST).transform.position, animationController))
            .AddPrecondition(beliefs["RestInFollowRange"])
            .AddEffect(beliefs["RestInInteractionRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("Rest")
            .WithStrategy(new RestStrategy(3, agentStats, animationController))
            .AddPrecondition(beliefs["RestInInteractionRange"])
            .AddEffect(beliefs["IsRested"])
            .Build());


        // - - - - - MINING - - - - -

        actions.Add(new GAgentAction.Builder("SearchForDeposit")
            .WithStrategy(new WanderStrategy(navMeshAgent, 10, animationController))
            .AddPrecondition(beliefs["IsRested"])
            .AddPrecondition(beliefs["IsEager"])
            .AddEffect(beliefs["DepositInFollowRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("MoveToDeposit")
            .WithStrategy(new FollowStrategy(navMeshAgent, () => { 
                followSensor.TryGetFreeDeposit(agentStats.ID, out Deposit deposit); 
                return deposit.transform.position; 
            }, animationController))
            .AddPrecondition(beliefs["IsRested"])
            .AddPrecondition(beliefs["IsEager"])
            .AddPrecondition(beliefs["DepositInFollowRange"])
            .AddEffect(beliefs["DepositInInteractionRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("Mine")
            .WithStrategy(new MineStrategy(5, agentStats, animationController, interactionSensor))
            .AddPrecondition(beliefs["IsEager"])
            .AddPrecondition(beliefs["DepositInInteractionRange"])
            .AddEffect(beliefs["HasOre"])
            .AddEffect(beliefs["HasFullOre"])
            .Build());
            
        // - - - - - BUILDING - - - - -

        actions.Add(new GAgentAction.Builder("SearchForBuilding")
            .WithStrategy(new WanderStrategy(navMeshAgent, 10, animationController))
            .AddPrecondition(beliefs["IsRested"])
            .AddPrecondition(beliefs["HasFullOre"])
            .AddEffect(beliefs["BuildingInFollowRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("MoveToBuilding")
            .WithStrategy(new FollowStrategy(navMeshAgent, () => followSensor.GetNearestTarget(BeaconType.BUILDING).transform.position, animationController)) // Should be handled differently. This must be the same target as in the belief. And needs to be avaiable to build. 
            .AddPrecondition(beliefs["IsRested"])
            .AddPrecondition(beliefs["HasFullOre"])
            .AddPrecondition(beliefs["BuildingInFollowRange"])
            .AddEffect(beliefs["BuildingInInteractionRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("Build")
            .WithStrategy(new BuildStrategy(5, agentStats, animationController, interactionSensor))
            .AddPrecondition(beliefs["BuildingInInteractionRange"])
            .AddPrecondition(beliefs["HasFullOre"])
            .AddEffect(beliefs["BuildingIsComplete"])
            .Build());

        // - - - - - PLAYER MARKER - - - - -

        actions.Add(new GAgentAction.Builder("MoveToMarker")
            .WithStrategy(new FollowStrategy(navMeshAgent, () => PlayerInteraction.instance.GetMarkerPosition(), animationController))
            .AddPrecondition(beliefs["MarkerExists"])
            .AddEffect(beliefs["MarkerInInteractionRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("WaitForOrders")
            .WithStrategy(new IdleStrategy(1))
            .AddPrecondition(beliefs["MarkerExists"])
            .AddPrecondition(beliefs["MarkerInInteractionRange"])
            .AddEffect(beliefs["IsWaitingForOrders"])
            .Build());

        // - - - - - FIGHT - - - - -

        actions.Add(new GAgentAction.Builder("MoveToEnemy")
            .WithStrategy(new FollowStrategy(navMeshAgent, () => {
                followSensor.TryGetEnemyStats(agentStats.TeamId, out var enemyStats);
                return enemyStats.transform.position;
            }, animationController))
            .AddPrecondition(beliefs["EnemyInFollowRange"])
            .AddEffect(beliefs["EnemyInInteractionRange"])
            .Build());

        actions.Add(new GAgentAction.Builder("Attack")
            .WithStrategy(new AttackStrategy(agentStats, animationController, interactionSensor, transform))
            .AddPrecondition(beliefs["EnemyInInteractionRange"])
            .AddEffect(beliefs["NoEnemyInRange"])
            .Build());
    }

    void SetupGoals() {
        goals = new HashSet<GAgentGoal>();

        goals.Add(new GAgentGoal.Builder("ChillOut")
            .WithPriority(0)
            .WithDesiredEffect(beliefs["Nothing"])
            .Build());

        goals.Add(new GAgentGoal.Builder("Wander")
            .WithPriority(1)
            .WithDesiredEffect(beliefs["Moving"])
            .Build());

        goals.Add(new GAgentGoal.Builder("KeepHealthUp")
            .WithPriority(10)
            .WithDesiredEffect(beliefs["IsHealthy"])
            .Build());

        goals.Add(new GAgentGoal.Builder("KeepStaminaUp")
            .WithPriority(5)
            .WithDesiredEffect(beliefs["IsRested"])
            .Build());

        goals.Add(new GAgentGoal.Builder("CollectOre")
            .WithPriority(3)
            .WithDesiredEffect(beliefs["HasFullOre"])
            .Build());

        goals.Add(new GAgentGoal.Builder("BuildBuildings")
            .WithPriority(4)
            .WithDesiredEffect(beliefs["BuildingIsComplete"])
            .Build());

        goals.Add(new GAgentGoal.Builder("FollowInstructions")
            .WithPriority(99)
            .WithDesiredEffect(beliefs["IsWaitingForOrders"])
            .Build());

        goals.Add(new GAgentGoal.Builder("DefendYourself")
            .WithPriority(15)
            .WithDesiredEffect(beliefs["NoEnemyInRange"])
            .Build());
    }

    void OnPlayerSelectionChanged() {
        // ReevaluatePlan();
    }

    void OnPlayerSelectionMarkerChanged() {
        if(PlayerInteraction.instance.SelectedAgent == gameObject) {
            ReevaluatePlan();
        }
    }

    public void ReevaluatePlan() {
        // Force the planner to re-evaluate the plan
        CurrentAction?.Interrupt();
        CurrentGoal = null;
        CurrentAction = null;
        animationController?.StopAnimating();
        animationController?.ResetSpeed();
    }

    void OnTargetsChanged(BeaconType type) {
        if(type == BeaconType.AGENT) {
            if(followSensor.TryGetEnemyStats(agentStats.TeamId, out _) || interactionSensor.TryGetEnemyStats(agentStats.TeamId, out _)) {
                ReevaluatePlan();
            }
        }
    }

    void Update() {
        if(goals == null || actions == null || beliefs == null) {
            Debug.LogError("Agent not fully initialized, skipping update");
            return;
        };

        // Update the plan and current action if there is one
        if(CurrentAction == null) {
            Debug.Log("Calculating any potential new plan");
            LogCurrentAgentState();
            CalculatePlan();

            if(actionPlan != null && actionPlan.Actions.Count > 0) {
                navMeshAgent.ResetPath();

                CurrentGoal = actionPlan.AgentGoal;
                Debug.Log($"Goal: {CurrentGoal.Name} with {actionPlan.Actions.Count} actions in plan");
                CurrentAction = actionPlan.Actions.Pop();
                Debug.Log($"Popped action: {CurrentAction.Name}");

                if (CurrentAction.Preconditions.All(b => b.Evaluate())) {
                    CurrentAction.Start();
                } else {
                    Debug.Log("Preconditions not met, clearing current action and goal");
                    CurrentAction = null;
                    CurrentGoal = null;
                }
            }
        }

        // If we have a current action, execute it
        if (actionPlan != null && CurrentAction != null) {
            CurrentAction.Update(Time.deltaTime);

            if (CurrentAction.Completed) {
                Debug.Log($"{CurrentAction.Name} complete");
                CurrentAction.Stop();
                CurrentAction = null;

                if (actionPlan.Actions.Count == 0) {
                    Debug.Log("Plan complete");
                    lastGoal = CurrentGoal;
                    CurrentGoal = null;
                }
            }
        }
    }

    void LogCurrentAgentState() {
        string beliefText = "";
        foreach(var belief in beliefs) {
            if(belief.Value.Evaluate()) {
                beliefText += $"{belief.Key} ";
            }
        }
        Debug.Log($"Beliefs: {beliefText}");
        Debug.Log($"Health: {agentStats.Health}/{agentStats.MaxHealth}, Stamina: {agentStats.Stamina}/{agentStats.MaxStamina}, Ore: {agentStats.Ore}/{agentStats.MaxOre}");
    }

    void CalculatePlan() {
        var priorityLevel = CurrentGoal?.Priority ?? 0;
        var goalsToConsider = goals;

        // If we have a current goal, we only want to consider goals with a higher priority
        if(CurrentGoal != null) {
            Debug.Log("Current goal exists, checking goals with higher priority");
            goalsToConsider = new HashSet<GAgentGoal>(goals.Where(g => g.Priority > priorityLevel));
        }

        var potentialPlan = goapPlanner.Plan(this, goalsToConsider, lastGoal);
        if(potentialPlan != null) {
            actionPlan = potentialPlan;
        }
    }

    public event Action<GAgentGoal> GoalChanged;
    public event Action<GAgentAction> ActionChanged;

    public HashSet<GAgentAction> Actions => actions;

    public GAgentGoal CurrentGoal {
        get => _currentGoal;
        set {
            _currentGoal = value;
            GoalChanged?.Invoke(_currentGoal);
        }
    }
    public GAgentAction CurrentAction {
        get => _currentAction;
        set {
            _currentAction = value;
            ActionChanged?.Invoke(_currentAction);
        }
    }

    private void OnDestroy() {
        PlayerInteraction.instance.SelectionChanged -= OnPlayerSelectionChanged;
    }
}
