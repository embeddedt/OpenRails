﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace ORTS.TrackViewer.Editing
{
    
    /// <summary>
    /// Class to define common methods related to autoconnecting a path. Autoconnecting means really searching
    /// for a possible connection between two nodes, and creating the path if the user wants to.
    /// </summary>
    public class AutoConnectTools
    {
        /// <summary>The list/collection of junction indexes that are not allowed during path auto-fixing</summary>
        private Collection<int> DisAllowedJunctionIndexes { get; set; }

        /// <summary>maximum number of nodes we will try before we conclude not reconnection is possible.</summary>
        private const int maxNumberNodesToCheckForAutoFix = 20;

        /// <summary>The node that is connected from, does that need to be reverse to enable the connection?</summary>
        protected bool FromNodeNeedsReverse;
        /// <summary>The node that is connected to, does that need to be reverse to enable the connection?</summary>
        protected bool ToNodeNeedsReverse;

        #region private members to store the path
        /// <summary>List of TVNs that describe how to connect two nodes (via which TrackVectorNodes the connection is made)</summary>
        List<int> linkingTvns;
        /// <summary>True when the connection is made on the same track from one vector node to the next</summary>
        bool sameTrackConnect;

        internal ConnectableNode autoConnectFromNode;
        internal ReconnectNodeOptions autoConnectToNodeOptions;
        #endregion

        #region public methods
        /// <summary>Constructor</summary>
        public AutoConnectTools()
        {
            DisAllowedJunctionIndexes = new Collection<int>();
            linkingTvns = new List<int>();
        }

        /// <summary>
        /// Reset the list of junctions that are not allowed while trying to find a connection
        /// </summary>
        public void ResetDisallowedJunctions()
        {
            DisAllowedJunctionIndexes.Clear();
        }

        /// <summary>
        /// Add a junction that is not allowed in the reconnection path
        /// </summary>
        /// <param name="junctionIndex">The index of the junction that is not allowed</param>
        public void AddDisallowedJunction(int junctionIndex)
        {
            DisAllowedJunctionIndexes.Add(junctionIndex);
        }
  
        /// <summary>
        /// Try to find a connection between two given nodes. Depth-first search via main track at junctions.
        /// Also reversing the start or reconnectNode is tried, in case one of these nodes has a non-defined orientation 
        /// because both before and after the node the path is broken.
        /// </summary>
        /// <param name="fromNode">Node at which the reconnection should start</param>
        /// <param name="toNode">Node at which the reconnection should end</param>
        /// <returns>True if a connection has been found</returns>
        public bool FindConnection(TrainpathNode fromNode, TrainpathNode toNode)
        {
            return FindConnection(fromNode, toNode, null);
        }

        /// <summary>
        /// Try to find a connection between two given nodes. Depth-first search via main track at junctions.
        /// Also reversing the start or reconnectNode is tried, in case one of these nodes has a non-defined orientation 
        /// because both before and after the node the path is broken.
        /// </summary>
        /// <param name="fromNode">Node at which the reconnection should start</param>
        /// <param name="toNode">Node at which the reconnection should end</param>
        /// <param name="firstTvnIndex">In case defined, the index of the first TVN the path has to follow</param>
        /// <returns>True if a connection has been found</returns>
        public bool FindConnection(TrainpathNode fromNode, TrainpathNode toNode, int? firstTvnIndex)

        {
            // We try to find a connection between two non-broken nodes.
            // We store the connection as a stack of linking tvns (track-node-vector-indexes)
            // The connection will only contain junctions (apart from maybe start and end=reconnect nodes)
            
            autoConnectFromNode = new ConnectableNode(fromNode, true, true);
            autoConnectToNodeOptions = new ReconnectNodeOptions(true);
            autoConnectToNodeOptions.AddNode(toNode, false); // only one option here

            return FindConnectionFromTo(firstTvnIndex, false);
        }

        /// <summary>
        /// Try to find a connection between a previously defined startNode and previously defined possible endNodes.
        /// Depth-first search via main track at junctions.
        /// Also reversing the start or single endNode is tried, in case one of these nodes has a non-defined orientation 
        /// because both before and after the node the path is broken.
        /// </summary>
        /// <param name="firstTvnIndex">If non-null define the first TVN index that needs to be followed</param>
        /// <param name="allowOnlyStartReverse">If true, only start node is allowed to be reversed</param>
        /// <returns>Whether a path has been found</returns>
        protected bool FindConnectionFromTo(int? firstTvnIndex, bool allowOnlyStartReverse)
        {

            if (FindConnectionSameTrack(firstTvnIndex))
            {
                return true;
            }

            //first try to see if we succeed without re-orienting the startNode or reconnectNode
            if (FindConnectionThisOrientation(firstTvnIndex))
            {
                return true;
            }

            //perhaps there is a path with a reversed start node.
            if (allowOnlyStartReverse || CanReverse(autoConnectFromNode.OriginalNode))
            {
                autoConnectFromNode.ReverseOrientation();
                FromNodeNeedsReverse = FindConnectionThisOrientation(firstTvnIndex);
                autoConnectFromNode.ReverseOrientation(); // we only do the actual reverse in CreateFoundConnection
                if (FromNodeNeedsReverse)
                {
                    return true;
                }
            }

            //perhaps there is a path with a reversed reconnect node.
            ConnectableNode reconnectNode = autoConnectToNodeOptions.SingleNode();
            if ((reconnectNode != null) && !allowOnlyStartReverse && CanReverse(reconnectNode.OriginalNode))
            {
                reconnectNode.ReverseOrientation();
                ToNodeNeedsReverse = FindConnectionThisOrientation(firstTvnIndex);
                reconnectNode.ReverseOrientation(); // we only do the actual reverse in CreateFoundConnection
                if (ToNodeNeedsReverse)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Actually create the path by linking nodes following the stored linking tvns.
        /// </summary>
        /// <param name="modificationTools">The tool set that is used to actually modify the path</param>
        /// <param name="isMainPath">Do we add the node to the main path or not</param>
        public void CreateFoundConnection(ModificationTools modificationTools, bool isMainPath)
        {
            CreateFoundConnection(modificationTools, isMainPath, false);
        }

        /// <summary>
        /// Actually create the path by linking nodes following the stored linking tvns.
        /// </summary>
        /// <param name="modificationTools">The tool set that is used to actually modify the path</param>
        /// <param name="isMainPath">Do we add the node to the main path or not</param>
        /// <param name="isFromNodeDirectionOK">Set this to true when the FromNode has already been set to correct orientation elsewhere</param>
        public void CreateFoundConnection(ModificationTools modificationTools, bool isMainPath, bool isFromNodeDirectionOK)
        {
            ConnectableNode autoConnectToNode = autoConnectToNodeOptions.ActualReconnectNode;


            if (FromNodeNeedsReverse && !isFromNodeDirectionOK)
            {
                autoConnectFromNode.ReverseOrientation();
            }
            if (ToNodeNeedsReverse)
            {
                autoConnectToNode.ReverseOrientation();
            }

            if (!autoConnectToNode.IsConnectingForward)
            {
                linkingTvns.Reverse();
                ConnectableNode swap = autoConnectToNode;
                autoConnectToNode = autoConnectFromNode;
                autoConnectFromNode = swap;
            }

            
            TrainpathNode currentNode = autoConnectFromNode.OriginalNode;
            
            if ((currentNode is TrainpathVectorNode) && !sameTrackConnect)
            {   // in case the first node is a vector node (and not a direct connect), go to its junction first
                currentNode = modificationTools.AddAdditionalNode(currentNode, isMainPath);
            }

            //create the new path using the stored Tvns
            foreach (int tvn in linkingTvns)
            {
                currentNode = modificationTools.AddAdditionalNode(currentNode, tvn, isMainPath);
                while (currentNode is TrainpathVectorNode)
                {   // apparently a disambiguity node has been added.
                    currentNode = modificationTools.AddAdditionalNode(currentNode, tvn, isMainPath);
                }
            }

            //make the final connections
            TrainpathNode toNode = autoConnectToNode.OriginalNode;
            modificationTools.StitchTwoPaths(currentNode, toNode, isMainPath);
        }

        /// <summary>
        /// Find the nodes that can be used to relink for a siding path, or a 'take-other-exit' path, ...
        /// The reconnecing nodes all have to be before the first special node (wait, uncouple, reverse, end).
        /// They also have to be before the end of the (current path), even if it does not have a formal end,
        /// and they have to be before a possible next siding start. At last, it needs to be a non-facing junction.
        /// (similar conditions apply for searching backwards.
        /// </summary>
        /// <param name="startNode">Node on train path to start searching</param>
        /// <param name="searchForward">Do you want the reconnect nodes forward or backwards along the path?</param>
        /// <param name="includeLastVectorNode">Is a vectorNode (start, end, wait, reverse) allowed?</param>
        /// <returns>List of possible reconnect nodes. Might be empty</returns>
        public List<TrainpathNode> FindReconnectNodeCandidates(TrainpathNode startNode, bool searchForward, bool includeLastVectorNode)
        {
            List<TrainpathNode> reconnectNodeCandidates = new List<TrainpathNode>();

            TrainpathNode mainNode = startNode;
            //follow the train path and see what we find
            while (true)
            {
                mainNode = searchForward ? mainNode.NextMainNode : mainNode.PrevNode;
                if (mainNode == null) break;

                TrainpathJunctionNode mainNodeAsJunction = mainNode as TrainpathJunctionNode;

                if (mainNodeAsJunction == null)
                {
                    if (mainNode.NodeType != TrainpathNodeType.Other)
                    {   // if it is not an other-node (so not a disambiguity node), stop searching
                        if (includeLastVectorNode)
                        {
                            reconnectNodeCandidates.Add(mainNode);
                        }
                        break;
                    }
                }
                else
                {
                    if (searchForward)
                    {
                        if (mainNode.NodeType == TrainpathNodeType.SidingStart)
                        {   // if a new siding path is started, stop searching
                            break;
                        }
                        if (!mainNodeAsJunction.IsFacingPoint)
                        {   // add the trailing junction.
                            reconnectNodeCandidates.Add(mainNode);
                        }
                        if (mainNode.NodeType == TrainpathNodeType.SidingEnd)
                        {   // for a main path we cannot reconnect past the end of the current siding path, but the siding end itself is still allowed
                            // for adding a passing path this should never happen
                            break;
                        }
                    }
                    else // searching backward
                    {
                        if (mainNode.NodeType == TrainpathNodeType.SidingEnd)
                        {   // if a new siding path is started (looking backwards), stop searching
                            break;
                        }
                        if (mainNodeAsJunction.IsFacingPoint)
                        {   // add the facing junction.
                            reconnectNodeCandidates.Add(mainNode);
                        }
                        if (mainNode.NodeType == TrainpathNodeType.SidingStart)
                        {   // for a main path we cannot reconnect before the start of the current siding path, but the siding end itself is still allowed
                            // for adding a passing path this should never happen
                            break;
                        }
                    }
                }
            }

            return reconnectNodeCandidates;
        }

        #endregion

        #region private methods
        /// <summary>
        /// Try to find a connection on the same track (vector node to vector node). If found, the corresponding path will be stored
        /// </summary>
        /// <param name="firstTvnIndex">In case defined, the index of the first TVN the path has to follow from the current junction. Do not use when starting on a tvnindex</param>
        /// <remarks>autoConnectFromNodes and autoConnectToNodeOptions are assumed to be defined already.
        /// </remarks>
        /// <returns>true if connection was found</returns>
        private bool FindConnectionSameTrack(int? firstTvnIndex)
        {
            linkingTvns.Clear();
            sameTrackConnect = true;
            bool foundConnection = autoConnectToNodeOptions.FoundConnectionSameTrack(autoConnectFromNode, firstTvnIndex);
            return foundConnection;
        }
            
        /// <summary>
        /// Try to find a connection. Depth-first search via main track at junctions. Stores the found connection in a list of 
        /// TrackNodeVectorIndexes (tvn's). No reversing of the nodes will be allowed. 
        /// </summary>
        /// <param name="firstTvnIndex">In case defined, the index of the first TVN the path has to follow from the current junction. Do not use when starting on a tvnindex</param>
        /// <remarks>autoConnectFromNodes and autoConnectToNodeOptions are assumed to be defined already.
        /// </remarks>
        /// <returns>True if a connection has been found</returns>
        private bool FindConnectionThisOrientation(int? firstTvnIndex)
        {
            linkingTvns.Clear();
            sameTrackConnect = false;
            
            //search for a path
            bool foundConnection;
            if (firstTvnIndex.HasValue)
            {
                foundConnection = TryToFindConnectionVia(autoConnectFromNode.ConnectingJunctionIndex, firstTvnIndex.Value);
            }
            else
            {
                foundConnection = TryToFindConnection(autoConnectFromNode.ConnectingJunctionIndex, autoConnectFromNode.IsConnectingJunctionFacing);
            }

            return foundConnection;
        }

        /// <summary>
        /// Try to find a connection between the current junction and a reconnect junction.
        /// We do a depth-first search, using the main tracks first.
        /// The result (the path) is stored in a list of linking tvns.
        /// In case there are DisAllowedJunctionIndexes we will not allow the connection to go over these junctions
        /// </summary>
        /// <param name="currentJunctionIndex">Index of the current junction</param>
        /// <param name="currentJunctionIsFacing">true if the current junction is a facing junction</param>
        /// <returns>true if a path was found</returns>
        private bool TryToFindConnection(int currentJunctionIndex, bool currentJunctionIsFacing)
        {

            if (autoConnectToNodeOptions.FoundConnection(currentJunctionIndex, currentJunctionIsFacing))
            {
                return autoConnectToNodeOptions.ConnectionIsGood;
            }

            // Did we go as deep as we want wanted to go?
            if (linkingTvns.Count == maxNumberNodesToCheckForAutoFix)
            {
                return false;
            }

            // Search further along the next Tvns that we can try.
            MSTS.Formats.TrackNode tn = TrackExtensions.TrackNode(currentJunctionIndex);
            if (tn.TrEndNode)
            {
                return false;
            }

            if (currentJunctionIsFacing)
            {
                //for debugging it is better to have multiple lines
                bool found;
                found = TryToFindConnectionVia(currentJunctionIndex, tn.MainTvn());
                if (found) return true;
                found = TryToFindConnectionVia(currentJunctionIndex, tn.SidingTvn());
                return found;
            }
            else
            {
                return TryToFindConnectionVia(currentJunctionIndex, tn.TrailingTvn());
            }
        }

        /// <summary>
        /// Try to find a connection between the current junction and a reconnect junction, along the given TVN
        /// We do a depth-first search, using the main tracks first.
        /// The result (the path) is stored in a list of linking tvns. 
        /// </summary>
        /// <param name="nextTvn">The TVN (Track Vector Node index) that we will take.</param>
        /// <param name="currentJunctionIndex">Index of the current junction</param>
        /// <returns>true if a path was found</returns>
        private bool TryToFindConnectionVia(int currentJunctionIndex, int nextTvn)
        {
            if (nextTvn <= 0) return false; // something wrong in train database.

            int nextJunctionIndex = TrackExtensions.GetNextJunctionIndex(currentJunctionIndex, nextTvn);
            if (DisAllowedJunctionIndexes.Contains(nextJunctionIndex))
            {
                return false;
            }
            bool nextJunctionIsFacing = (nextTvn == TrackExtensions.TrackNode(nextJunctionIndex).TrailingTvn());

            linkingTvns.Add(nextTvn);
            bool succeeded = TryToFindConnection(nextJunctionIndex, nextJunctionIsFacing);
            if (!succeeded)
            {   //Pop the index that did not work
                linkingTvns.RemoveAt(linkingTvns.Count - 1);
            }

            return succeeded;
        }
        
        /// <summary>
        /// Can a node in a path be reversed without breaking something?
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        private static bool CanReverse(TrainpathNode node)
        {
            bool outgoingAllowsReversal;
            if (node.NextSidingNode != null)
            {   // if there is a siding node, this is a siding start (probably) and things become too complex
                outgoingAllowsReversal = false;
            }
            else
            {
                if (node.NextMainNode == null)
                {   // no next main node, so we are fine with reversing
                    outgoingAllowsReversal = true;
                }
                else
                {
                    outgoingAllowsReversal = node.NextMainNode.IsBroken || (node.NextMainTvnIndex == -1);
                }
            }

            bool incomingAllowsReversal;
            if (node.PrevNode == null)
            {
                incomingAllowsReversal = true;
            }
            else
            {
                incomingAllowsReversal = node.PrevNode.IsBroken || (node.PrevNode.NextMainTvnIndex == -1);
            }

            return incomingAllowsReversal && outgoingAllowsReversal;
        }
        
        #endregion

        #region debugMethods
        /// <summary>
        /// Return a string describing the track vector node (TVN) indexes that are being used for a connection
        /// </summary>
        public string LinkingTvnsAsString()
        {
            return String.Join(",", this.linkingTvns.Select(o => o.ToString()).ToArray());
        }
        #endregion
    }

    /// <summary>
    /// Class to support dynamic or continuous auto-connecting. This means that a set of possible reconnection
    /// nodes is determined once, and that reconnecting is being tried dynamically.
    /// This allows the Start node to be changed (e.g. regarding exact location on track, or perhaps moved to another
    /// track), and still be able to find reconnections.
    /// </summary>
    public class ContinuousAutoConnecting:AutoConnectTools
    {
        /// <summary>The Start/From node needs to be reversed to be able to make the connection</summary>
        public bool NeedsReverse { get { return this.FromNodeNeedsReverse;} }
        private bool isForward;

        static Dictionary<bool, Drawing.DebugWindow> debugWindows = new Dictionary<bool, Drawing.DebugWindow>();
        static ContinuousAutoConnecting()
        {
            debugWindows[true] = new Drawing.DebugWindow(10, 40);
            debugWindows[false] = new Drawing.DebugWindow(10, 60);
        }
        private string debugString = String.Empty;

        /// <summary>
        /// Constructor. This will also find store the candidates for reconnecting
        /// </summary>
        /// <param name="startNode">The node to start from for reconnection. Only used for initial determination of possible reconnection nodes</param>
        /// <param name="isConnectingForward">Is this node connecting forwards (along the path) or not</param>
        public ContinuousAutoConnecting(TrainpathNode startNode, bool isConnectingForward)
        {
            
            isForward = isConnectingForward;
            List<TrainpathNode> reconnectNodes = this.FindReconnectNodeCandidates(startNode, isConnectingForward, true);
            autoConnectToNodeOptions = new ReconnectNodeOptions(isConnectingForward);
            foreach (TrainpathNode node in reconnectNodes)
            {
                autoConnectToNodeOptions.AddNode(node, false);
                debugString += node.ToStringShort();
            }

        }

        /// <summary>
        /// Determine if a connection can be made (and store the found solution)
        /// </summary>
        /// <param name="fromNode"></param>
        /// <returns></returns>
        public bool CanConnect(TrainpathVectorNode fromNode)
        {
            autoConnectFromNode = new ConnectableNode(fromNode, true, isForward);
            FromNodeNeedsReverse = false; // reset to correct value
            bool canIndeedConnect = FindConnectionFromTo(null, true);
            debugWindows[isForward].DrawString = String.Format("{0}:{1} ({2}) {3}", canIndeedConnect,
                (autoConnectToNodeOptions.ActualReconnectNode == null ? "none" : autoConnectToNodeOptions.ActualReconnectNode.OriginalNode.ToStringShort()),
                LinkingTvnsAsString(), debugString);
            return canIndeedConnect;
        }
    }
}