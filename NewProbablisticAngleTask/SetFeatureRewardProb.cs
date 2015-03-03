using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RatControl;
using System.Xml.Linq;
using System.Threading;

namespace AsheshPlugins
{
    public class SetFeatureRewardProb : IEntryPlugin
    {
        private int EvalStateID, EvalStateSubFSM;
        private int ToneStateID, ToneStateSubFSM;
        private int RewardStateID, RewardStateSubFSM;
        private int FailureStateID, FailureStateSubFSM;
        private double MinDist;
        public int TrialHistory;
        private string VarMeasure;
        public double UB, T, LB;
        private int completed;
        private int max_reward, min_reward;

        // Random reward inputs
        private int NormTrials, ProbTrials;
        public bool Randomreward;
        private double RewardProb;
        public int trialcount = 0;

        public void Parse(XElement x)
        {
            EvalStateID = (int)x.Element("Evaluation").Attribute("ID");
            EvalStateSubFSM = (int)x.Element("Evaluation").Attribute("SubFSM");
            ToneStateID = (int)x.Element("Tone").Attribute("ID");
            ToneStateSubFSM = (int)x.Element("Tone").Attribute("SubFSM");
            RewardStateID = (int)x.Element("Reward").Attribute("ID");
            RewardStateSubFSM = (int)x.Element("Reward").Attribute("SubFSM");
            FailureStateID = (int)x.Element("Failure").Attribute("ID");
            FailureStateSubFSM = (int)x.Element("Failure").Attribute("SubFSM");
            max_reward = (int)x.Element("Reward").Attribute("MaxPulses"); // (ul)
            min_reward = (int)x.Element("Reward").Attribute("MinPulses"); // (ul)

            UB = (double)x.Element("Parameters").Attribute("Upper");
            T = (double)x.Element("Parameters").Attribute("Target");
            LB = (double)x.Element("Parameters").Attribute("Lower");
            MinDist = (double)x.Element("Parameters").Attribute("MinDist");

            // inputs of the random reward scheme
            NormTrials = (int)x.Element("Conditions").Attribute("NormalTrials");
            ProbTrials = (int)x.Element("Conditions").Attribute("ProbTrials");
            RewardProb = (double)x.Element("Conditions").Attribute("RewardProbability");
            Randomreward = (bool)x.Element("Conditions").Attribute("RandomReward");
        }

        Random rnd = new Random();
        private bool RandOn = false;
        public int[] PastRewards = new int[1000];
        public int PastRewardscount = 0;

        public XElement Execute(RatControlExpt RCExpt)
        {
            var JP = RCExpt.eventPlugins[0] as UpdateJoystickPosition_xyFeature;
            FSMState BeforeState = RCExpt.FSMStates[EvalStateSubFSM][EvalStateID];
            FSMState RewardState = RCExpt.FSMStates[RewardStateSubFSM][RewardStateID];

            int numofpulses;

            trialcount++;

            if ((trialcount > NormTrials) & (trialcount <= (NormTrials + ProbTrials)) & (Randomreward))
            {
                // Trial is rewarded or unrewarded at random
                RandOn = true;
                if (RewardProb > rnd.NextDouble())
                {
                    numofpulses = PastRewards[rnd.Next(PastRewardscount)];
                    if (numofpulses > 0)
                    {
                        BeforeState.timerTransition = ToneStateID;
                        RewardState.numPulses[0] = numofpulses;
                    }
                    else
                    {
                        numofpulses = 0;
                        BeforeState.timerTransition = FailureStateID;
                    }
                }
                else
                {
                    numofpulses = 0;
                    BeforeState.timerTransition = FailureStateID;
                }
            }
            else
            {
                // Normal trials, but also record the reward history so it can be used for random reward selection.
                RandOn = false;
                        
                //////////////////////
                if ((JP.bottomed) & (JP.maxDist >= MinDist))
                {
                    completed = 1;
                    if ((JP.feature < UB) & (JP.feature > LB))
                    {
                        if (JP.feature >= T)
                        {
                            numofpulses = (int)Math.Ceiling((max_reward - min_reward) * (UB - JP.feature) / (UB - T)) + min_reward;
                        }
                        else
                        {
                            numofpulses = (int)Math.Ceiling((max_reward - min_reward) * (JP.feature - LB) / (T - LB)) + min_reward;
                        }

                        BeforeState.timerTransition = ToneStateID;
                        RewardState.numPulses[0] = numofpulses;

                        // Record reward for random reward selection later in session
                        if (trialcount <= NormTrials)
                        {
                            PastRewards[PastRewardscount] = numofpulses;
                            PastRewardscount++;
                        }
                    }
                    else
                    {
                        BeforeState.timerTransition = FailureStateID;
                        numofpulses = 0;
                    }
                }

                else
                {
                    completed = 0;
                    BeforeState.timerTransition = FailureStateID;
                    numofpulses = 0;
                }
            }
            var x = new XElement("SetRewardPluginDetails", new XAttribute("RewardProb", RewardProb), new XAttribute("RandOn", RandOn), new XAttribute("Dist", JP.maxDist), new XAttribute("Reward", numofpulses), new XAttribute("SampleNum", JP.trial_begin), new XAttribute("Timeout", JP.bottomed), new XAttribute("Feature", JP.feature), new XAttribute("Completed", completed));
            return x;
        }
    }   
}
