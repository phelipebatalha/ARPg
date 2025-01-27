﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using XNode;
using UnityEngine;
using Random = UnityEngine.Random;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine.Playables;
namespace DialogueSystem
{
    public class DialogueHandler
    {
        /* Readonly */
        public TextAsset Asset { get; private set; }
        public bool IsAnimating { get { return ui.isAnimating; } }
        public bool IsRunning { get; set; }
        public MonoBehaviour AttachedMonoBehaviour { get; private set; }
        public DialogueState CurrentState { get; private set; }
        public Playable CurrentPlayable { get; private set; }

        /*Private */
        private DialogueState PreviousState;
        private double _cachedPlayableSpeed = 1f;

        /*
         * Public  
        */
        public int currentDialogueChoiceNum;

        /*
         * Graph 
         */
        public DialogueGraph CurrentGraph;
        public BranchNode CurrentBranch { get; private set; }
        public BaseNode CurrentNode { get; private set; }
        public BaseNode PreviousNode { get; private set; }
        public List<BranchNode> CurrentValidBranches { get; private set; }
        private BaseNode currentTextNode;
        private BaseNode currentTimelineEventNode;

        /*
         * Settings
        */
        public DialogueSettings settings;
        public DialogueCallbackActions callbackActions;
        public DialogueDictionary dictionary;

        /*
         * Enums
         */

        public enum GraphTraversalDirection
        {
            Forward,
            Backward
        }

        public enum DialogueEventType
        {
            OnTextNodeEnter,
            OnTextStart,
            OnTextEnd,
            OnTextNodeLeave,
            OnEventNodeEnter,
            OnEventNodeLeave,
            OnBranchEnter,
            OnBranchLeave,
            OnDialogueEnter,
            OnDialogueLeave,
            OnNodeEnter,
            OnNodeLeave,
        }

        StringBuilder str = new StringBuilder();

        public List<GameEventFlag> gameEventFlags { get; set; }
        public List<DialogueGameState> gameStateVariables;
        private DialogueUI ui;

        /* Higher order functions? */
        public Func<DialogueConditional, bool> CustomTestCondition { get; set; }
        public Func<DialogueChoices, bool> OnChoiceDraw;


        public DialogueHandler(DialogueUI ui, DialogueSettings settings = null, DialogueCallbackActions callbackActions = null, DialogueDictionary dictionary = null, MonoBehaviour monoBehaviour = null)
        {
            this.ui = ui;
            this.settings = settings ?? new DialogueSettings();
            this.callbackActions = callbackActions ?? new DialogueCallbackActions();
            this.dictionary = dictionary ?? new DialogueDictionary();
            this.AttachedMonoBehaviour = monoBehaviour;

             //Debug.Log(gameStateVariables == null ? "gameStateVariables is NULL in DialogueHandler" : "gameStateVariables initialized in DialogueHandler");
        }
        public async Task StartDialogueWithNPC(DialogueGraph newGraph)
        {
            // Finaliza o diálogo atual, se houver algum
            if (IsRunning)
            {
                EndDialogue();
            }

            // Executa o novo gráfico do NPC
            await ExecuteGraph(newGraph);
        }

        public async Task ExecuteGraph(DialogueGraph graph)
        {
            CurrentGraph = graph;
            GetPossibleBranches();
    
            if (CurrentValidBranches.Count != 0)
            {
                IsRunning = true;
                CurrentState = DialogueState.Running;
                SetCurrentBranch();

                // Aplicar a configuração de seleção de ramificação
                if (settings != null)
                {
                    if (settings.multipleValidBranchesSelectionMode == DialogueSettings.MultipleValidBranchesSelectionMode.FIRST)
                    {
                        // Garantir que a primeira ramificação válida seja selecionada
                        SelectFirstValidBranch();
                    }
                }

                InvokeCallbacks(DialogueEventType.OnDialogueEnter);
        
                TraverseGraph();
            }
            else
            {
                Debug.LogWarning("No valid branch found.");
            }
        }

        private void SelectFirstValidBranch()
        {
            if (CurrentValidBranches.Count > 0)
            {
                // Se houver ramificações válidas, selecione a primeira
                CurrentBranch = CurrentValidBranches[0];
                this.SetCurrentBranch();
            }
        }

        
        public void TraverseGraph(GraphTraversalDirection direction = GraphTraversalDirection.Forward)
        {
            if (CurrentNode == null)
            {
                if (CurrentBranch == null)
                {
                    Debug.LogError("Cannot travese graph - No branch set!");
                    return;
                }
                CurrentNode = CurrentBranch;
            }
            string nodeType = CurrentNode.NodeType;

            if (PreviousNode != null && !PreviousNode.processed)
            {
                InvokeCallbacks(DialogueEventType.OnNodeLeave);
            }

            if (!CurrentNode.entered) InvokeCallbacks(DialogueEventType.OnNodeEnter);

            if (nodeType == "BranchNode")
            {
                BaseNode nextNode = GetNextNode(CurrentNode);
                if (nextNode != null)
                {
                    CurrentNode = nextNode;
                    TraverseGraph();
                } else
                {
                    EndDialogue();
                }

            }

            if (nodeType == "TextNode"){
//                Debug.LogError("TEXT");
                HandleTextNode();
            }

            if (nodeType == "ConditionalNode")
            {
                ConditionalNode node = CurrentNode as ConditionalNode;
                bool result = EvaluateConditionalNode(node);

                NodePort output = result ? CurrentNode.GetOutputPort("trueOutput") : CurrentNode.GetOutputPort("falseOutput");
                NodePort connectingNode = output.Connection;
                if (connectingNode != null)
                {
                    PreviousNode = CurrentNode;
                    CurrentNode = connectingNode.node as BaseNode;
                    TraverseGraph();
                }
                else
                {
                    EndDialogue();
                }
                
            }

            if (nodeType == "ChoiceNode")
            {
                HandleChoiceNode(CurrentNode as ChoiceNode);

            }

            if (nodeType == "EventNode")
            {
                ui.ShowDialoguePane(false);
                HandleEventNode(CurrentNode as EventNode);
            }

            if (nodeType == "WaitNode")
            {
                WaitNode node = CurrentNode as WaitNode;
                if (AttachedMonoBehaviour != null)
                {
                    CurrentState = DialogueState.Waiting;
                    if (node.hideWindow) ui.ShowDialoguePane(false);
                    AttachedMonoBehaviour.StartCoroutine(WaitNode(node.time, OnWaitNodeEndCallback));
                }
                else
                {
                    Debug.LogError("DialogueHandler has no attached MonoBehaviour. Wait node will be skipped.");
                    OnWaitNodeEndCallback();
                }
            }

            if(nodeType == "TimelineEventNode")
            {
                if(currentTimelineEventNode == null)
                {
                    currentTimelineEventNode = CurrentNode;
                    TimelineEventNode node = CurrentNode as TimelineEventNode;
                    if (node.timelineEvent == TimelineEventNode.TimelineEvent.Resume)
                    {
                        if (node.HideDialoguePanelOnResume) ui.ShowDialoguePane(false);
                        if (node.PauseDialogueOnResume) CurrentState = DialogueState.PausedByTimeline;
                        PauseCurrentPlayable(false);
                    }

                } 
                else
                {
                    BaseNode nextNode = GetNextNode(currentTimelineEventNode);
                    currentTimelineEventNode = null;
                    if(nextNode != null)
                    {
                        CurrentNode = nextNode;
                        //INVOKE
                        TraverseGraph();
                    } else
                    {
                        EndDialogue();
                    }
                }

            }
        }

        public void HandleTextNode()
        {
            if (currentTextNode == null)
            {
                InvokeCallbacks(DialogueEventType.OnTextNodeEnter);
                currentTextNode = CurrentNode;
                TextNode textNode = currentTextNode as TextNode;
                string text = ProcessText(CurrentNode.GetString()[0]);
                if (textNode.audioClip != null && textNode.syncTypewriter)
                {
                    float speedOverride = textNode.audioClip.length / text.Length;
                    DisplayText(text, settings.textDisplayMode, speedOverride);
                }
                else
                {
                    DisplayText(text, settings.textDisplayMode);
                }
            }
            else
            {
                BaseNode nextNode = GetNextNode(currentTextNode);
                currentTextNode = null;
                if (nextNode != null)
                {
                    CurrentNode = nextNode;
                    InvokeCallbacks(DialogueEventType.OnTextNodeLeave);
                    TraverseGraph();
                }
                else
                {
                    EndDialogue();
                }
                
            }
        }

        public void HandleChoiceNode(ChoiceNode node, int choiceNum = -1)
        {
            if (node == null)
            {
                Debug.LogError("Choice node is not set.");
                return;
            }

            ChoiceNode choiceNode = CurrentNode as ChoiceNode;

            if (choiceNum == -1)
            {
                if (OnChoiceDraw != null)
                {
                    CurrentState = DialogueState.AwaitingChoice;
                    OnChoiceDraw.Invoke(choiceNode.GetChoices());
                }
                else
                {
                    throw new MissingMethodException("No OnChoiceDraw method specified for ChoiceNode.");
                }
                return;
            }

            if (choiceNum >= 0 && choiceNum <= node.choices.Count)
            {
                NodePort output = choiceNode.GetOutputPort($"choices {choiceNum}");
                NodePort connectingNode = output.Connection;
                if (connectingNode != null)
                {
                    PreviousNode = CurrentNode;
                    CurrentNode = connectingNode.node as BaseNode;
                    TraverseGraph();
                }
                else
                {
                    EndDialogue();
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException($"choice numer {choiceNum} is not out of range. Found {node.choices.Count} available choices.");
            }
        }

        public void HandleEventNode(EventNode node)
        {
            if (CurrentState == DialogueState.AwaitingEventResponse)
            {
                BaseNode nextNode = GetNextNode(node);
                if (nextNode != null)
                {

                    CurrentNode = nextNode;
                    CurrentState = DialogueState.Running;
                    InvokeCallbacks(DialogueEventType.OnEventNodeLeave);
                    ui.ShowDialoguePane(true);
                    TraverseGraph();

                }
                else
                {
                    EndDialogue();
                }
            } 
            else
            {
                if (!node.GoToNextNodeAutomatically)
                {
                    CurrentState = DialogueState.AwaitingEventResponse;
                }

                if (callbackActions.EventHandler == null)
                {
                    Debug.LogError("No dialogue event handler is registered.");
                }

                InvokeCallbacks(DialogueEventType.OnEventNodeEnter);
                foreach (DialogueEvent dialogueEvent in node.events)
                {
                    callbackActions.EventHandler?.Invoke(dialogueEvent);
                }

                if (node.GoToNextNodeAutomatically)
                {
                    BaseNode nextNode = GetNextNode(node);
                    if (nextNode != null)
                    {
                        CurrentNode = nextNode;
                        ui.ShowDialoguePane(true);
                        TraverseGraph();
                    }
                    else
                    {
                        ui.ShowDialoguePane(false);
                        EndDialogue();
                    }
                }
            }
        }

        private BaseNode GetNextNode(BaseNode currentNode)
        {
            NodePort output = currentNode.GetOutputPort("output");
            NodePort connectingNode = output.Connection;
            if (connectingNode != null)
            {
                PreviousNode = currentNode;
                return connectingNode.node as BaseNode;

            }
            return null;
        }
        public void Cleanup()
        {
           
            Debug.Log("DialogueHandler cleaned up.");
        }

        public void OnWaitNodeEndCallback()
        {
            ui.ShowDialoguePane(true);
            BaseNode nextNode = GetNextNode(CurrentNode);
            if (nextNode != null)
            {
                CurrentNode = nextNode;
                TraverseGraph();
            }
            else
            {
                EndDialogue();
            }
        }
        private IEnumerator WaitNode(float seconds, Action onWaitEndCallback)
        {
            yield return new WaitForSeconds(seconds);
            onWaitEndCallback.Invoke();

        }

        public void EndDialogue()
        {
            IsRunning = false;
            CurrentState = DialogueState.NotRunning;
            
            // Limpa o estado atual para garantir que o próximo gráfico comece do início
            CurrentNode = null;
            CurrentBranch = null;
            CurrentGraph = null;

            // Reseta o UI e outros componentes visuais
            ui.ShowDialoguePane(false);
            ui.Reset();
    
            // Invoca callbacks relacionados ao fim do diálogo
            InvokeCallbacks(DialogueEventType.OnBranchLeave);
            InvokeCallbacks(DialogueEventType.OnDialogueLeave);
            
        }

        private bool EvaluateConditionalNode(ConditionalNode node)
        {
            List<bool> evaluations = new List<bool>();
            foreach (DialogueConditional condition in node.condition)
            {
                if (CustomTestCondition != null)
                {
                    evaluations.Add(CustomTestCondition(condition));
                }
                else
                {
                    evaluations.Add(TestCondition(condition));
                }
            }

            return evaluations.TrueForAll(x => x);
        }

        public void SetUI(DialogueUI ui)
        {
            this.ui = ui;
        }

        public void SetCallbackActions(DialogueCallbackActions callbackActions)
        {
            this.callbackActions = callbackActions;
        }

        public void SetCurrentPlayable(Playable playable)
        {
            CurrentPlayable = playable;
        }

        public void PauseCurrentPlayable(bool pause)
        {
            if (pause)
            {
                _cachedPlayableSpeed = CurrentPlayable.GetGraph().GetRootPlayable(0).GetSpeed();
                CurrentPlayable.GetGraph().GetRootPlayable(0).SetSpeed(0);
            }
            else {
                CurrentPlayable.GetGraph().GetRootPlayable(0).SetSpeed(_cachedPlayableSpeed);
            }
        }

        public void ResumeDialoguePausedByTimeline()
        {
            if(CurrentState == DialogueState.PausedByTimeline)
            {
                CurrentState = DialogueState.Running;
            }
        }

        private void GetPossibleBranches()
        {
            List<BranchNode> validBranches = new List<BranchNode>();
            foreach (BranchNode branch in CurrentGraph.GetAllBranchNodes())
            {

                List<bool> evaluations = new List<bool>();
                foreach (DialogueConditional condition in branch.branchCondition)
                {
                    bool eval = false;
                    if (CustomTestCondition != null)
                    {
                        eval = CustomTestCondition(condition);
                    }
                    else
                    {
                        eval = TestCondition(condition);
                    }
                    evaluations.Add(eval);
                }

                if (evaluations.TrueForAll(x => x == true)) validBranches.Add(branch);
            }
            CurrentValidBranches = validBranches;
        }

        private void SetCurrentBranch()
        {
            if (CurrentValidBranches.Count > 0)
            {
                switch (settings.multipleValidBranchesSelectionMode)
                {
                    case DialogueSettings.MultipleValidBranchesSelectionMode.FIRST:
                        CurrentBranch = CurrentValidBranches[0];
                        break;
                    case DialogueSettings.MultipleValidBranchesSelectionMode.PRIORITY:
                        CurrentBranch = CurrentValidBranches[0];
                        break;
                    case DialogueSettings.MultipleValidBranchesSelectionMode.RANDOM:
                        int randInt = Random.Range(0, CurrentValidBranches.Count - 1);
                        CurrentBranch = CurrentValidBranches[randInt];
                        break;
                    default:
                        break;
                }
                InvokeCallbacks(DialogueEventType.OnBranchEnter);
            }
        }

        private bool TestCondition(DialogueConditional condition)
        {
            if(gameStateVariables == null){
                Debug.Log("AAAAAAAAAAAAAA");
            }
            DialogueGameState gameStateVariable = gameStateVariables.Find(x => x.name.ToLower() == condition.variable.ToLower());
            if (gameStateVariable == null)
            {
                Debug.LogWarning($"No game state variable with name {condition.variable} was found!");
                return false;
            }
            switch (condition.type)
            {
                case DialogueConditional.VariableType.INT:
                    if (gameStateVariable.ValueType != typeof(int))
                    {
                        Debug.LogError($"Condition {condition.variable} expected variable of type integer, got {gameStateVariable.ValueType}");
                        return false;
                    }
                    switch (condition.numberCondition)
                    {
                        case DialogueConditional.NumberCondition.GreaterThan:
                            return gameStateVariable.IntValue > condition.intTarget;
                        case DialogueConditional.NumberCondition.GreaterThanOrEqual:
                            return gameStateVariable.IntValue >= condition.intTarget;
                        case DialogueConditional.NumberCondition.LessThan:
                            return gameStateVariable.IntValue < condition.intTarget;
                        case DialogueConditional.NumberCondition.LessThanOrEqual:
                            return gameStateVariable.IntValue <= condition.intTarget;
                        case DialogueConditional.NumberCondition.EqualTo:
                            return gameStateVariable.IntValue == condition.intTarget;
                    }
                    break;
                case DialogueConditional.VariableType.BOOL:
                    if (gameStateVariable.ValueType != typeof(bool))
                    {
                        Debug.LogError($"Condition {condition.variable} expected variable of type boolean, got {gameStateVariable.ValueType}");
                        return false;
                    }
                    switch (condition.boolCondition)
                    {
                        case DialogueConditional.BoolCondition.FALSE:
                            return gameStateVariable.BoolValue == false;
                        case DialogueConditional.BoolCondition.TRUE:
                            return gameStateVariable.BoolValue == true;
                    }
                    break;
                case DialogueConditional.VariableType.FLOAT:
                    if (gameStateVariable.ValueType != typeof(float))
                    {
                        Debug.LogError($"Condition {condition.variable} expected variable of type float, got {gameStateVariable.ValueType}");
                        return false;
                    }
                    switch (condition.numberCondition)
                    {
                        case DialogueConditional.NumberCondition.GreaterThan:
                            return gameStateVariable.FloatValue > condition.floatTarget;
                        case DialogueConditional.NumberCondition.GreaterThanOrEqual:
                            return gameStateVariable.FloatValue >= condition.floatTarget;
                        case DialogueConditional.NumberCondition.LessThan:
                            return gameStateVariable.FloatValue < condition.floatTarget;
                        case DialogueConditional.NumberCondition.LessThanOrEqual:
                            return gameStateVariable.FloatValue <= condition.floatTarget;
                        case DialogueConditional.NumberCondition.EqualTo:
                            return gameStateVariable.FloatValue == condition.floatTarget;
                    }
                    break;
                case DialogueConditional.VariableType.STRING:
                    if (gameStateVariable.ValueType != typeof(string))
                    {
                        Debug.LogError($"Condition {condition.variable} expected variable of type string, got {gameStateVariable.ValueType}");
                        return false;
                    }
                    switch (condition.stringCondition)
                    {
                        case DialogueConditional.StringCondition.EqualTo:
                            return gameStateVariable.StringValue == condition.stringTarget;
                        case DialogueConditional.StringCondition.EqualToIgnoreCasing:
                            return gameStateVariable.StringValue.ToLower() == condition.stringTarget.ToLower();
                        case DialogueConditional.StringCondition.NotEqualTo:
                            return gameStateVariable.StringValue != condition.stringTarget;
                        case DialogueConditional.StringCondition.NotEqualToIgnoreCasing:
                            return gameStateVariable.StringValue.ToLower() != condition.stringTarget.ToLower();
                    }
                    return false;
            }
            return false;
        }

        public void DisplayText(string text, TextEffects.TextDisplayMode textAnimation, float? typewriterSpeedOverride = null)
        {
            if (textAnimation != TextEffects.TextDisplayMode.INSTANT) CurrentState = DialogueState.Animating;
            ui.SetDialogueText(text, textAnimation, InvokeCallbacks, typewriterSpeedOverride);
        }

        private void InvokeCallbacks(DialogueEventType eventType)
        {

            if (callbackActions != null)
            {

                if (eventType == DialogueEventType.OnTextStart)
                {
                    TextNode node = currentTextNode as TextNode;
                    callbackActions.OnTextNodeStart?.Invoke(node);
                    //callbackActions.OnTextNodeEndEvents?.Invoke(node);
                }

                if (eventType == DialogueEventType.OnTextEnd)
                {
                    TextNode node = currentTextNode as TextNode;
                    callbackActions.OnTextNodeEnd?.Invoke(node);
                    callbackActions.OnTextNodeEndEvents?.Invoke(node);
                }

                if (eventType == DialogueEventType.OnBranchLeave)
                {
                    callbackActions.OnBranchNodeLeave?.Invoke(CurrentBranch);
                    callbackActions.OnBranchNodeEndEvents?.Invoke(CurrentBranch);
                }

                if (eventType == DialogueEventType.OnDialogueLeave)
                {
                    callbackActions.OnDialogueGraphEnd?.Invoke(CurrentGraph);
                    callbackActions.OnDialogueGraphEndEvents?.Invoke(CurrentGraph);
                }

                if (eventType == DialogueEventType.OnNodeEnter)
                {
                    CurrentNode.entered = true;
                    callbackActions.OnNodeEnter?.Invoke(CurrentNode);
                }

                if (eventType == DialogueEventType.OnNodeLeave)
                {
                    PreviousNode.processed = true;
                    callbackActions.OnNodeLeave?.Invoke(PreviousNode);
                }

            }

            if (eventType == DialogueEventType.OnTextEnd)
            {
                TextNode node = currentTextNode as TextNode;
                if (!node.WaitForInput)
                {
                    TraverseGraph();
                }

                else
                {
                    CurrentState = DialogueState.Idle;
                }
            }
        }

        public void Pause(bool toggle)
        {
            if (CurrentState != DialogueState.Paused && toggle == true)
            {
                PreviousState = CurrentState;
            }
            CurrentState = toggle ? DialogueState.Paused : PreviousState;
            ui.Pause(toggle);
        }

        string ProcessText(string line)
        {

            str.Clear();
            ui.Reset();

            Regex dictionaryRegex = new Regex(@"<.*?\/>");
            Regex waitRegex = new Regex(@"%.*?\/%");


            MatchCollection dictionaryRegexMatches = dictionaryRegex.Matches(line);
            string interpolatedText = ReplaceWords(dictionaryRegexMatches, line);

            List<int> CmdObjectIndex = interpolatedText.AllIndexesOf("{");

            int index = 0;
            for (int i = 0; i < interpolatedText.Length; ++i)
            {
                if (CmdObjectIndex.Contains(i))
                {
                    int cmdEndIndex = interpolatedText.IndexOf('}', i);
                    string substring = interpolatedText.Substring(i, cmdEndIndex - i + 1);
                    TextCommand cmd = JsonUtility.FromJson<TextCommand>(substring);
                    ui.RegisterTextEffectIndices(cmd, index, index + cmd.text.Length);
                    str.Append(cmd.text);
                    i = cmdEndIndex;
                    index += cmd.text.Length;
                }
                else
                {
                    str.Append(interpolatedText[i]);
                    index++;
                }
            }

            SetWaitIndicesRecursive(str);

            return str.ToString();
        }

        string ReplaceWords(MatchCollection matches, string originalString)
        {
            string interpolatedString = originalString;
            foreach (Match match in matches)
            {
                GroupCollection group = match.Groups;
                foreach (Group key in group)
                {
                    if (key.Value.Length <= 3 || string.IsNullOrWhiteSpace(key.Value.Substring(1, key.Value.Length - 3)))
                    {
                        Debug.LogWarning("Empty dictionary key found in text.");
                        continue;
                    }
                    else
                    {
                        string keyValue = key.Value.Substring(1, key.Value.Length - 3);
                        string dictionaryValue = dictionary.GetEntry(keyValue);
                        if (dictionaryValue != null)
                        {
                            interpolatedString = interpolatedString.Replace(key.Value, dictionaryValue);
                        }
                    }
                }
            }

            return interpolatedString;
        }

        void SetWaitIndicesRecursive(StringBuilder sb)
        {
            Regex waitRegex = new Regex(@"%.*?\/%");
            Match match = waitRegex.Match(sb.ToString());

            if(match.Success)
            {
                Group key = match.Groups[0];

                string waitString = key.Value.Substring(1, key.Value.Length - 3);
                if (float.TryParse(waitString, out float waitTime))
                {
                    
                    sb.Replace(key.Value, "", match.Index, key.Value.Length);
                    ui.RegisterWaitIndex(match.Index - 1, key.Value.Length, waitTime);
                    SetWaitIndicesRecursive(sb);
                }
                else
                {
                    Debug.LogError($"Invalid wait command found: {waitString}");
                }
            }
            
        }

    }

    [Serializable]
    public class TextCommand
    {
        public string effect;
        public string color;
        public string text;
    }
}


