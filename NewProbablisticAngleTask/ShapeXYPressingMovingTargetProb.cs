using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Diagnostics;
using RatControlOp.Common;
using RatControlEventDetails;

namespace RatControlOp.Scripts
{
    public class ShapeXYPressingMovingTarget : EmptyScript
    {
        // State Machine parameter names
        const string lowerString = "LowerBound";
        const string upperString = "UpperBound";
        const string targetString = "Target";
        const string RandomRewardstring = "RandomReward";
        const string RewardProbstring = "RewardProbability";
        const string normtrialstring = "NormalTrials";
        const string probtrialstring = "ProbTrials";
        const string trialblockstring = "TrialBlock";
        const string order0string = "Order0";
        const string order1string = "Order1";
        const string order2string = "Order2";

        // Script Input parameters
        private double Upper; // Upper Bound
        private double Lower; // Lower Bound
        private double Target; // Target
        private double MinRewarded; // minimum percentage rewarded, otherwise make easier
        private double MaxRewarded; // maximum percentage rewarded, otherwise make harder
        private int MinNumOfTrials; // minimum number of trials in previous session necc. to assess performance, else keep old boundaries
        private double pTarget; // percentile to shift to (~35 %)
        private int JSPressState; // FSM state indicating joystick press
        private int JSPressSubFSM; // subFSM ID indicating joystick press
        private int RewardState; // FSM state indicating reward delivery
        private int RewardSubFSM; // subFSM ID indicating reward delivery
        private bool Shaping; // to shape (true) or not to shape (false)
        private double TargetEdgeDist; // Distance from center (0) to maximum target displacement
        private double MinJumpDist; // Minimum jump distance for target
        private double BoundThreshJump; // Minimum distance between target and boundary for target jump

        // Script Internal Params
        private bool SMStarted;
        private int n, nRew;

        private enum Status { Easy, Good, Hard};

        //Display in script
        public double LB;
        public double UB;
        public double T;

        // Random reward parameters
        private int NormTrials; // # trials for normal reward block
        private int ProbTrials; // # trials for probabilistic reward block
        private int BlockJitter; // # Actual Normal trials and probablistic trials will be +- this number
        private double RewardProb1; // Reward Probability in block 1
        private double RewardProb2; // Reward Probability in block 2
        private double RewardProb3; // Reward Probability in block 3
        private bool RandomReward; // Random reward on or off. If off, no max limit on trials per session
        private int numBlocks;
        public int[] blockorder = new int[3];
        public double RewardProb;


        public override void ParseParameters()
        {
            Upper = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Upper Bound").Value);
            Lower = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Lower Bound").Value);
            Target = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Target").Value);
            MinRewarded = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Min % Rewarded").Value);
            MaxRewarded = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Max % Rewarded").Value);
            MinNumOfTrials = int.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Min Num of Trials").Value);
            pTarget = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "% Target").Value);
            JSPressState = int.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Joystick Pressed State").Value);
            JSPressSubFSM = int.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Joystick Pressed Sub FSM").Value);
            RewardState = int.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Reward State").Value);
            RewardSubFSM = int.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Reward Sub FSM").Value);
            Shaping = bool.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Shape?").Value);
            TargetEdgeDist = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Edge Distance").Value);
            MinJumpDist = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Min Jump Distance").Value);
            BoundThreshJump = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Bound Threshold for Jump").Value);


            // inputs of the random reward scheme
            RandomReward = bool.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Random Reward?").Value);
            NormTrials = int.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Normal Trials").Value);
            ProbTrials = int.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Prob Trials").Value);
            BlockJitter = int.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Block Jitter").Value);
            RewardProb1 = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Reward Prob 1").Value);
            RewardProb2 = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Reward Prob 2").Value);
            RewardProb3 = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Reward Prob 3").Value);
            numBlocks = double.Parse(ScriptParameters.First(a => a.ScriptParameter.Name == "Blocks in Cycle").Value);
        }

        public override SMCompletionStatus IsComplete(out List<SMParameterValue> newParams)
        {
            Status status = Status.Good;

            newParams = null;

            if (!SMStarted)
                using (RatControlDataContext db = new RatControlDataContext())
                {
                    int exptIDEval = ExptID; // exptID used for evaluating performance
                    int defIDEval = 0; // defID used for evaluating performance

                    // First check to see if there was a previous session in this experiment
                    var defIDs = ScriptHelper.GetStageDefIDs(ExptID, null, false, db);
                    if ((defIDs.Count() > 0) & Shaping)
                    {
                        int DefID = defIDs.Max();
                        exptIDEval = ExptID;
                        defIDEval = DefID;
                        status = evaluatePerformance(exptIDEval, defIDEval, db);
                    }
                    else
                    {
                        newParams = useScriptParameters(); // use script parameters
                    }

                    //////////// Random Reward Code ////////////
                    int[] defidcount = db.ExecuteQuery<int>(@"SELECT COUNT(Definition.value('(//@TrialBlock)[1]', 'int')) AS param6 FROM StateMachines WHERE ExptID = {0} AND Type = 4 AND Definition.value('(//@NormalTrials)[1]', 'int') > 0", ExptID.ToString()).ToArray();
                    
                    // Retrieve old script parameters
                    if (defidcount[0] > 0)
                    {
                        // Recover block parameters passed from previous session
                        var sql = db.ExecuteQuery<Parameter>(@"SELECT Definition.value('(//@TrialBlock)[1]', 'int') AS param6, Definition.value('(//@Order0)[1]', 'int') AS param7, Definition.value('(//@Order1)[1]', 'int') AS param8, Definition.value('(//@Order2)[1]', 'int') AS param9 FROM StateMachines WHERE ExptID = {0} AND Type = 4 AND Definition.value('(//@NormalTrials)[1]', 'int') > 0", ExptID.ToString()).ToArray();
                        int lindex = sql.Length - 1;

                        // Current block number
                        trblock = sql[lindex].param6;

                        // These are the block numbers of the blocks that will have random reward
                        blockorder[0] = sql[lindex].param7;
                        blockorder[1] = sql[lindex].param8;
                        blockorder[2] = sql[lindex].param9;
                    }

                    // Initialize block numbers
                    if (trblock == 0)
                    {
                      

                         // initialize block order
                        Random rnd = new Random();
                        int numBlocks = 8;
                        int[] blockorder = new int[numBlocks];
                        double[] tmpOrder = new double[numBlocks];
                        int[] tmpVals = new int[numBlocks];


                        // initialize block order
                        for (i = 0; i < numBlocks; i++)
                        {
                            tmpOrder[i] = rnd.NextDouble();
                            tmpVals[i] = i;
                        }
                        // Find the indices of the lowest 3. These will be the random reward blocks
                        Array.Sort(tmpOrder, tmpVals);
                        blockorder[0] = tmpVals[0];
                        blockorder[1] = tmpVals[1];
                        blockorder[2] = tmpVals[2];

                        string tmpString = "Block Sequence: ";
                        for (int i = 0; i < 3; i++)
                        {
                            tmpString = tmpString + blockorder[i];
                            if (i < numBlocks - 1)
                            {
                                tmpString = tmpString + ",";
                            }
                        }
                        Trace.WriteLine(tmpString);
                    }

                    // Add some randomness to the number of normal and prob trials: +- 10
                    NormTrials = defNormTrials + Math.round(2 * BlockJitter * rnd.NextDouble()) - BlockJitter;
                    ProbTrials = defProbTrials + Math.round(2 * BlockJitter * rnd.NextDouble()) - BlockJitter;

                    // Set reward probability, if the current block is one of the 3 reward blocks
                    if (blockorder[0] == trblock)
                    {
                        RandomReward = 1;
                        RewardProb = RewardProb1;
                    }
                    else if (blockorder[1] == trblock)
                    {
                        RandomReward = 1;
                        RewardProb = RewardProb2;
                    }
                    else if (blockorder[2] == trblock)
                    {
                        RandomReward = 1;
                        RewardProb = RewardProb3;
                    }
                    else
                    {
                        RandomReward = 0;
                        RewardProb = -1;
                    }
                    Trace.WriteLine("Reward Probability = " + RewardProb);

                    // Update trblock
                    switch (trblock)
                    {
                        case (numBlocks - 1):
                            trblock = 0;
                            break;
                        default:
                            trblock++;
                            break;
                    }
                    newParams = updateRandom();

                    // Update script parameters
                    /*
                    if (defidcount[0] > 0)
                    {
                        if ((LB == 0) || (UB == 0))
                        {
                            newParams = updateRandom();
                        }
                        else
                        {
                            newParams = updateRandom();
                        }
                    }
                    else
                    {
                        newParams = updateRandom();
                    }
                    */


                    //////////////////////////////////



                    switch (status)
                    {
                        case Status.Good:
                            Trace.WriteLine("Good");
                            if ((LB == 0) || (UB == 0))
                            {
                                newParams = useScriptParameters();
                            }
                            else
                            {
                                newParams = SMParameters.Select(a => new SMParameterValue { StateMachineParameter = a.StateMachineParameter, Value = a.Value }).ToList();
                                newParams.First(a => a.StateMachineParameter.Name == lowerString).Value = Convert.ToString(LB);
                                newParams.First(a => a.StateMachineParameter.Name == upperString).Value = Convert.ToString(UB);
                                newParams.First(a => a.StateMachineParameter.Name == targetString).Value = Convert.ToString(T);
                            }
                            break;
                        case Status.Easy:
                            if ((LB == 0) || (UB == 0))
                            {
                                newParams = useScriptParameters();
                            }
                            newParams = changeBoundaries(exptIDEval, defIDEval, db);
                            if ((UB - LB) / 2 < BoundThreshJump) // check for jump
                            {
                                Trace.WriteLine("Jumping Target");
                                newParams = jumpTarget(exptIDEval, defIDEval, db);
                                newParams = changeBoundaries(exptIDEval, defIDEval, db);
                            }
                            else // just easy
                            {
                                Trace.WriteLine("Too Easy");
                             }
                            break;
                        case Status.Hard:
                            Trace.WriteLine("Too Hard");
                            if ((LB == 0) || (UB == 0))
                            {
                                newParams = useScriptParameters();
                            }
                            newParams = changeBoundaries(exptIDEval, defIDEval, db);
                            break;
                        default:
                            break;
                    }

                    Trace.WriteLine("New Upper Bound = " + UB + ", New Lower Bound = " + LB + ", Target = " + T);
                    SMStarted = true;
                    return SMCompletionStatus.SubStageComplete;
                }
                else
                {
                    return SMCompletionStatus.Incomplete;
                }
        }

        private Status evaluatePerformance(int exptID, int defID, RatControlDataContext db)
        {
            int numOfRewardedTrials;
            double percentRewarded;
            // number of trials in previous session
            int numOfTrials = ScriptHelper.CountNumEntries(exptID, JSPressState, defID, JSPressSubFSM, db);
            Trace.WriteLine("Number of Trials: " + numOfTrials);
            
            // Evaluate number of trials and percentage
            if (numOfTrials < MinNumOfTrials)
            {
                return Status.Good;
                Trace.WriteLine("Too Few Trials");
            }
            else
            {
                numOfRewardedTrials = ScriptHelper.CountNumEntries(exptID, RewardState, defID, RewardSubFSM, db);
                percentRewarded = (double)numOfRewardedTrials / (double)numOfTrials * 100;
                Trace.WriteLine("Percentage Rewarded: " + percentRewarded + "%");
                
                if (percentRewarded < MinRewarded)
                {
                    return Status.Hard;
                }
                else if (percentRewarded > MaxRewarded)
                {
                    return Status.Easy;
                }
                else
                {
                    return Status.Good;
                }
            }
        }

        private List<SMParameterValue> useScriptParameters()
        {
            List<SMParameterValue> newParams;
            newParams = SMParameters.Select(a => new SMParameterValue { StateMachineParameter = a.StateMachineParameter, Value = a.Value }).ToList();
            newParams.First(a => a.StateMachineParameter.Name == lowerString).Value = Convert.ToString(Lower);
            newParams.First(a => a.StateMachineParameter.Name == upperString).Value = Convert.ToString(Upper);
            newParams.First(a => a.StateMachineParameter.Name == targetString).Value = Convert.ToString(Target);
            LB= Lower;
            UB = Upper;
            T = Target;
            return newParams;
        }
        private List<SMParameterValue> updateRandom()
        {
            List<SMParameterValue> newParams;
            newParams = SMParameters.Select(a => new SMParameterValue { StateMachineParameter = a.StateMachineParameter, Value = a.Value }).ToList();

            // Update random reward parameters
            newParams.First(a => a.StateMachineParameter.Name == RandomRewardstring).Value = Convert.ToString(RandomReward);
            newParams.First(a => a.StateMachineParameter.Name == normtrialstring).Value = Convert.ToString(NormTrials);
            newParams.First(a => a.StateMachineParameter.Name == probtrialstring).Value = Convert.ToString(ProbTrials);
            newParams.First(a => a.StateMachineParameter.Name == trialblockstring).Value = Convert.ToString(trblock);
            newParams.First(a => a.StateMachineParameter.Name == order0string).Value = Convert.ToString(blockorder[0]);
            newParams.First(a => a.StateMachineParameter.Name == order1string).Value = Convert.ToString(blockorder[1]);
            newParams.First(a => a.StateMachineParameter.Name == order2string).Value = Convert.ToString(blockorder[2]);
            newParams.First(a => a.StateMachineParameter.Name == RewardProbstring).Value = Convert.ToString(RewardProb);
            return newParams;
        }
        private List<SMParameterValue> changeBoundaries(int exptID, int defID, RatControlDataContext db)
        {
            int? FirstRCEID = null;
            int? LastRCEID = null;
            db.GetFirstRCE(exptID, defID, ref FirstRCEID);
            db.GetLastRCE(exptID, defID, ref LastRCEID);

            if (FirstRCEID == null || LastRCEID == null)
            {
                Trace.WriteLine("RCEIDs are null");
            }
            else
            {
                
                var Features = db.ExecuteQuery<double?>(@"SELECT Details.value('(//SetRewardPluginDetails/@Feature)[1]', 'float') AS A1 FROM RatControlEvents WHERE ExptID = {0} AND RatEventsID >= {1} AND RatEventsID <= {2} AND EventType = 9 ORDER BY A1 DESC", exptID.ToString(), FirstRCEID.ToString(), LastRCEID.ToString()).ToArray();
                var Completed = db.ExecuteQuery<int?>(@"SELECT Details.value('(//SetRewardPluginDetails/@Completed)[1]', 'int') AS A1 FROM RatControlEvents WHERE ExptID = {0} AND RatEventsID >= {1} AND RatEventsID <= {2} AND EventType = 9 ORDER BY A1 DESC", exptID.ToString(), FirstRCEID.ToString(), LastRCEID.ToString()).ToArray();

                int nevents = Features.Count();

                int n = 0;
                for (int i = 0; i < nevents; i++)
                {
                    if ((Features[i] != null) & (Completed[i] == 1)) { n++; }
                }

                // Convert angles into distance from target angle
                Double[] Dists = new double[n];

                n = 0;
                for (int i=0; i < nevents; i++) 
                {
                    if ((Features[i] != null) & (Completed[i] == 1))
                    {
                        Dists[n] = Math.Abs(T - (double)Features[i]);
                        n++;
                    }
                }

                // Sort the distances from closest to farthest
                Array.Sort(Dists);

                // Find the distance of the 35% farthest one
                int psampnum = (int)Math.Round(n * pTarget/100);
                var w = Dists[psampnum];

                LB = T - w;
                UB = T + w;
            }
            
            List<SMParameterValue> newParams;
            newParams = SMParameters.Select(a => new SMParameterValue { StateMachineParameter = a.StateMachineParameter, Value = a.Value }).ToList();
            newParams.First(a => a.StateMachineParameter.Name == lowerString).Value = Convert.ToString(LB);
            newParams.First(a => a.StateMachineParameter.Name == upperString).Value = Convert.ToString(UB);
            newParams.First(a => a.StateMachineParameter.Name == targetString).Value = Convert.ToString(T);
            return newParams;
        }

        private List<SMParameterValue> jumpTarget(int exptID, int defID, RatControlDataContext db)
        {
            int? FirstRCEID = null;
            int? LastRCEID = null;
            db.GetFirstRCE(exptID, defID, ref FirstRCEID);
            db.GetLastRCE(exptID, defID, ref LastRCEID);

            if (FirstRCEID == null || LastRCEID == null)
            {
                Trace.WriteLine("RCEIDs are null");
            }
            else
            {
                double upperEdge = TargetEdgeDist;  
                double lowerEdge = -TargetEdgeDist;
                double prevT = T; // Record previous target location before updating it.
                
                double bound1 = lowerEdge; // Bounds of available intervals
                double bound2 = prevT - MinJumpDist;
                double bound3 = prevT + MinJumpDist;
                double bound4 = upperEdge;
                double intvLen1 = 0; // Length of lower available interval made up by bound1 to bound2
                double intvLen2 = 0; // Length of upper available interval made up by bound3 to bound4

                // Keep interval length as zero if target is too close to the edge
                if (bound2 > bound1) 
                {
                    // previous target is very close to the upper edge 
                    intvLen1 = bound2 - bound1;
                }
                if (bound4 > bound3) 
                {
                    // previous target is very close to the lower edge 
                    intvLen2 = bound4 - bound3;
                }
                
                // Pick a random location within the two intervals
                Random random = new Random();
                double randomTargetLoc = random.NextDouble() * (intvLen1 + intvLen2);
                if (randomTargetLoc > intvLen1) 
                {
                    // previous target is very close to the upper edge 
                    T = bound3 + randomTargetLoc - intvLen1;
                } 
                else 
                {
                    // previous target is very close to the lower edge 
                    T = bound1 + randomTargetLoc;
                }
            }

            List<SMParameterValue> newParams;
            newParams = SMParameters.Select(a => new SMParameterValue { StateMachineParameter = a.StateMachineParameter, Value = a.Value }).ToList();
            newParams.First(a => a.StateMachineParameter.Name == targetString).Value = Convert.ToString(T);
            return newParams;
        }

    }
}