﻿/// COPYRIGHT 2010 by the Open Rails project.
/// This code is provided to enable you to contribute improvements to the open rails program.  
/// Use of the code for any other purpose or distribution of the code to anyone else
/// is prohibited without specific written permission from admin@openrails.org.
/// 
/// Principal Author:
///    Rick Grout
/// Contributors:
///    Walt Niehoff
///    

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.IO;
using System.Xml; 
using System.Xml.Schema;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.GamerServices;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Net;
using Microsoft.Xna.Framework.Storage;
using MSTS;

namespace ORTS
{
    #region Dynatrack
    public class Dynatrack
    {
        /// <summary>
        /// Decompose an MSTS multi-subsection dynamic track section into multiple single-subsection sections.
        /// </summary>
        /// <param name="viewer">Viewer reference.</param>
        /// <param name="dTrackList">DynatrackDrawer list.</param>
        /// <param name="dTrackObj">Dynamic track section to decompose.</param>
        /// <param name="worldMatrix">Position matrix.</param>
        public static void Decompose(Viewer3D viewer, List<DynatrackDrawer> dTrackList, DyntrackObj dTrackObj, 
            WorldPosition worldMatrix)
        {
            // DYNAMIC TRACK
            // =============
            // Objectives:
            // 1-Decompose multi-subsection DT into individual sections.  
            // 2-Create updated transformation objects (instances of WorldPosition) to reflect 
            //   root of next subsection.
            // 3-Distribute elevation change for total section through subsections. (ABANDONED)
            // 4-For each meaningful subsection of dtrack, build a separate DynatrackMesh.
            //
            // Method: Iterate through each subsection, updating WorldPosition for the root of
            // each subsection.  The rotation component changes only in heading.  The translation 
            // component steps along the path to reflect the root of each subsection.

            // The following vectors represent local positioning relative to root of original (5-part) section:
            Vector3 localV = Vector3.Zero; // Local position (in x-z plane)
            Vector3 localProjectedV; // Local next position (in x-z plane)
            Vector3 displacement;  // Local displacement (from y=0 plane)
            Vector3 heading = Vector3.Forward; // Local heading (unit vector)

            float realRun; // Actual run for subsection based on path


            WorldPosition nextRoot = new WorldPosition(worldMatrix); // Will become initial root
            Vector3 sectionOrigin = worldMatrix.XNAMatrix.Translation; // Save root position
            worldMatrix.XNAMatrix.Translation = Vector3.Zero; // worldMatrix now rotation-only

            // Iterate through all subsections
            for (int iTkSection = 0; iTkSection < dTrackObj.trackSections.Count; iTkSection++)
            {
                float length = dTrackObj.trackSections[iTkSection].param1; // meters if straight; radians if curved
                if (length == 0.0) continue; // Consider zero-length subsections vacuous

                // Create new DT object copy; has only one meaningful subsection
                DyntrackObj subsection = new DyntrackObj(dTrackObj, iTkSection);

                //uint uid = subsection.trackSections[0].UiD; // for testing

                // Create a new WorldPosition for this subsection, initialized to nextRoot,
                // which is the WorldPosition for the end of the last subsection.
                // In other words, beginning of present subsection is end of previous subsection.
                WorldPosition root = new WorldPosition(nextRoot);

                // Now we need to compute the position of the end (nextRoot) of this subsection,
                // which will become root for the next subsection.

                // Clear nextRoot's translation vector so that nextRoot matrix contains rotation only
                nextRoot.XNAMatrix.Translation = Vector3.Zero;

                // Straight or curved subsection?
                if (subsection.trackSections[0].isCurved == 0) // Straight section
                {   // Heading stays the same; translation changes in the direction oriented
                    // Rotate Vector3.Forward to orient the displacement vector
                    localProjectedV = localV + length * heading;
                    displacement = TDBTraveller.MSTSInterpolateAlongStraight(localV, heading, length,
                                                            worldMatrix.XNAMatrix, out localProjectedV);
                    realRun = length;
                }
                else // Curved section
                {   // Both heading and translation change 
                    // nextRoot is found by moving from Point-of-Curve (PC) to
                    // center (O)to Point-of-Tangent (PT).
                    float radius = subsection.trackSections[0].param2; // meters
                    Vector3 left = radius * Vector3.Cross(Vector3.Up, heading); // Vector from PC to O
                    Matrix rot = Matrix.CreateRotationY(-length); // Heading change (rotation about O)
                    // Shared method returns displacement from present world position and, by reference,
                    // local position in x-z plane of end of this section
                    displacement = TDBTraveller.MSTSInterpolateAlongCurve(localV, left, rot,
                                            worldMatrix.XNAMatrix, out localProjectedV);

                    heading = Vector3.Transform(heading, rot); // Heading change
                    nextRoot.XNAMatrix = rot * nextRoot.XNAMatrix; // Store heading change
                    realRun = radius * ((length > 0) ? length : -length); // Actual run (meters)
                }

                // Update nextRoot with new translation component
                nextRoot.XNAMatrix.Translation = sectionOrigin + displacement;

                // THE FOLLOWING COMMENTED OUT CODE IS NOT COMPATIBLE WITH THE NEW MESH GENERATION METHOD.
                // IF deltaY IS STORED AS ANYTHING OTHER THAN 0, THE VALUE WILL GET USED FOR MESH GENERATION,
                // AND BOTH THE TRANSFORMATION AND THE ELEVATION CHANGE WILL GET USED, IN ESSENCE DOUBLE COUNTING.
                /*
                // Update subsection ancillary data
                subsection.trackSections[0].realRun = realRun;
                if (iTkSection == 0)
                {
                    subsection.trackSections[0].deltaY = displacement.Y;
                }
                else
                {
                    // Increment-to-increment change in elevation
                    subsection.trackSections[0].deltaY = nextRoot.XNAMatrix.Translation.Y - root.XNAMatrix.Translation.Y;
                }
                */

                // Create a new DynatrackDrawer for the subsection
                dTrackList.Add(new DynatrackDrawer(viewer, subsection, root, nextRoot));
                localV = localProjectedV; // Next subsection
            }
        } // end Decompose

    } // end class Dynatrack
    #endregion

    #region DynatrackDrawer
    public class DynatrackDrawer
    {
        #region Class variables
        Viewer3D Viewer;
        WorldPosition worldPosition;
        public DynatrackMesh dtrackMesh;
        #endregion

        #region Constructor
		/// <summary>
		/// DynatrackDrawer constructor
		/// </summary>
		public DynatrackDrawer(Viewer3D viewer, DyntrackObj dtrack, WorldPosition position, WorldPosition endPosition)
		{
			Viewer = viewer;
			worldPosition = position;

			if (viewer.Simulator.TRP == null)
			{
				// First to need a track profile creates it
				Trace.Write(" TRP");
				// Creates profile and loads materials into SceneryMaterials
				TRPFile.CreateTrackProfile(viewer.RenderProcess, viewer.Simulator.RoutePath, out viewer.Simulator.TRP);
			}

			// Instantiate classes
			dtrackMesh = new DynatrackMesh(Viewer.RenderProcess, dtrack, worldPosition, endPosition);
		} // end DynatrackDrawer constructor
		
		/// <summary>
		/// DynatrackDrawer default constructor, without DyntrackObj
		/// </summary>
		public DynatrackDrawer(Viewer3D viewer, WorldPosition position, WorldPosition endPosition)
		{
			Viewer = viewer;
			worldPosition = position;

		} // end DynatrackDrawer constructor
		#endregion

        /// <summary>
        /// PrepareFrame adds any object mesh in-FOV to the RenderItemCollection. 
        /// and marks the last LOD that is in-range.
        /// </summary>
        public void PrepareFrame(RenderFrame frame, ElapsedTime elapsedTime)
        {
            // Offset relative to the camera-tile origin
            int dTileX = worldPosition.TileX - Viewer.Camera.TileX;
            int dTileZ = worldPosition.TileZ - Viewer.Camera.TileZ;
            Vector3 tileOffsetWrtCamera = new Vector3(dTileX * 2048, 0, -dTileZ * 2048);

            // Find midpoint between track section end and track section root.
            // (Section center for straight; section chord center for arc.)
            Vector3 xnaLODCenter = 0.5f * (dtrackMesh.XNAEnd + worldPosition.XNAMatrix.Translation +
                                            2 * tileOffsetWrtCamera);
            dtrackMesh.MSTSLODCenter = new Vector3(xnaLODCenter.X, xnaLODCenter.Y, -xnaLODCenter.Z);

            // Ignore any mesh not in field-of-view
            if (!Viewer.Camera.InFOV(dtrackMesh.MSTSLODCenter, dtrackMesh.ObjectRadius)) return;

            // Scan LODs in forward order, and find first LOD in-range
            LOD lod;
            int lodIndex;
            for (lodIndex = 0; lodIndex < dtrackMesh.TrProfile.LODs.Count; lodIndex++)
            {
                lod = (LOD)dtrackMesh.TrProfile.LODs[lodIndex];
                if (Viewer.Camera.InRange(dtrackMesh.MSTSLODCenter, 0, lod.CutoffRadius)) break;
            }
            if (lodIndex == dtrackMesh.TrProfile.LODs.Count) return;
            // lodIndex marks first in-range LOD

            // Initialize xnaXfmWrtCamTile to object-tile to camera-tile translation:
            Matrix xnaXfmWrtCamTile = Matrix.CreateTranslation(tileOffsetWrtCamera);
            xnaXfmWrtCamTile = worldPosition.XNAMatrix * xnaXfmWrtCamTile; // Catenate to world transformation
            // (Transformation is now with respect to camera-tile origin)

            int lastIndex;
            // Add in-view LODs to the RenderItems collection
            if (dtrackMesh.TrProfile.LODMethod == TrProfile.LODMethods.CompleteReplacement)
            {
                // CompleteReplacement case
                lastIndex = lodIndex; // Add only the LOD that is the first in-view
            }
            else
            {
                // ComponentAdditive case
                // Add all LODs from the smallest in-view CutOffRadius to the last
                lastIndex = dtrackMesh.TrProfile.LODs.Count - 1;
            }
            while (lodIndex <= lastIndex)
            {
                lod = (LOD)dtrackMesh.TrProfile.LODs[lodIndex];
                for (int j = lod.PrimIndexStart; j < lod.PrimIndexStop; j++)
                {
                    frame.AddPrimitive(dtrackMesh.ShapePrimitives[j].Material, dtrackMesh.ShapePrimitives[j],
                        RenderPrimitiveGroup.World, ref xnaXfmWrtCamTile, ShapeFlags.AutoZBias);
                }
                lodIndex++;
            }
        } // end PrepareFrame
    } // end DynatrackDrawer
    #endregion

    #region DynatrackProfile

    // A track profile consists of a number of groups used for LOD considerations.  There are LODs,
    // Levels-Of-Detail, each of which contains subgroups.  Here, these subgroups are called "LODItems."  
    // Each group consists of one of more "polylines".  A polyline is a chain of line segments successively 
    // interconnected. A polyline of n segments is defined by n+1 "vertices."  (Use of a polyline allows 
    // for use of more than single segments.  For example, a ballast LOD could be defined as left slope, 
    // level, right slope - a single polyline of four vertices.)
    #region TRPFile
    /// <summary>
    ///  Track profile file class
    /// </summary>
    public class TRPFile
    {
        public TrProfile TrackProfile; // Represents the track profile
        //public RenderProcess RenderProcess; // TODO: Pass this along in function calls

        /// <summary>
        /// Creates a TRPFile instance from a track profile file (XML or STF) or canned.
        /// (Precedence is XML [.XML], STF [.DAT], default [canned]).
        /// </summary>
        /// <param name="renderProcess">Render process.</param>
        /// <param name="routePath">Path to route.</param>
        /// <param name="trpFile">TRPFile created (out).</param>
        public static void CreateTrackProfile(RenderProcess renderProcess, string routePath, out TRPFile trpFile)
        {
            string path = routePath + @"\TrackProfiles";
            //Establish default track profile
            if (Directory.Exists(path) && File.Exists(path + @"\TrProfile.xml"))
            {
                // XML-style
                trpFile = new TRPFile(renderProcess, path + @"\TrProfile.xml");
            }
            else if (Directory.Exists(path) && File.Exists(path + @"\TrProfile.stf"))
            {
                // MSTS-style
                trpFile = new TRPFile(renderProcess, path + @"\TrProfile.stf");
            }
            else
            {
                // default
                trpFile = new TRPFile(renderProcess, "");
            }
            // FOR DEBUGGING: Writes XML file from current TRP
            //TRP.TrackProfile.SaveAsXML(@"C:/Users/Walt/Desktop/TrProfile.xml");
        } // end CreateTrackProfile

        /// <summary>
        /// Create TrackProfile from a track profile file.  
        /// (Defaults on empty or nonexistent filespec.)
        /// </summary>
        /// <param name="renderProcess">Render process.</param>
        /// <param name="filespec">Complete filepath string to track profile file.</param>
        public TRPFile(RenderProcess renderProcess, string filespec)
        {
            if (filespec == "")
            {
                // No track profile provided, use default
                TrackProfile = new TrProfile(renderProcess);
                Trace.Write("(default)");
                return;
            }
            FileInfo fileInfo = new FileInfo(filespec);
            if (!fileInfo.Exists)
            {
                TrackProfile = new TrProfile(renderProcess); // Default profile if no file
                Trace.Write("(default)");
            }
            else
            {
                string fext = filespec.Substring(filespec.LastIndexOf('.')); // File extension

                switch (fext.ToUpper())
                {
                    case ".STF": // MSTS-style
                        using (STFReader stf = new STFReader(filespec, false))
                        {
                            // "EXPERIMENTAL" header is temporary
                            if (stf.SimisSignature != "SIMISA@@@@@@@@@@JINX0p0t______")
                            {
                                STFException.TraceError(stf, "Invalid header - file will not be processed. Using DEFAULT profile.");
                                TrackProfile = new TrProfile(renderProcess); // Default profile if no file
                            }
                            else
                                try
                                {
                                    stf.ParseBlock(new STFReader.TokenProcessor[] {
                                        new STFReader.TokenProcessor("trprofile", ()=>{ TrackProfile = new TrProfile(renderProcess, stf); }),
                                    });
                                }
                                catch (Exception e)
                                {
                                    STFException.TraceError(stf, "Track profile STF constructor failed because " + e.Message + ". Using DEFAULT profile.");
                                    TrackProfile = new TrProfile(renderProcess); // Default profile if no file
                                }
                                finally
                                {
                                    if (TrackProfile == null)
                                    {
                                        STFException.TraceError(stf, "Track profile STF constructor failed. Using DEFAULT profile.");
                                        TrackProfile = new TrProfile(renderProcess); // Default profile if no file
                                    }
                                }
                        }
                        Trace.Write("(.STF)");
                        break;

                    case ".XML": // XML-style
                        // Convention: .xsd filename must be the same as .xml filename and in same path.
                        // Form filespec for .xsd file
                        string xsdFilespec = filespec.Substring(0, filespec.LastIndexOf('.')) + ".xsd"; // First part

                        // Specify XML settings
                        XmlReaderSettings settings = new XmlReaderSettings();
                        settings.ConformanceLevel = ConformanceLevel.Auto; // Fragment, Document, or Auto
                        settings.IgnoreComments = true;
                        settings.IgnoreWhitespace = true;
                        // Settings for validation
                        settings.ValidationEventHandler += new ValidationEventHandler(ValidationCallback);
                        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
                        settings.ValidationType = ValidationType.Schema; // Independent external file
                        settings.Schemas.Add("TrProfile.xsd", XmlReader.Create(xsdFilespec)); // Add schema from file

                        // Create an XML reader for the .xml file
                        using (XmlReader reader = XmlReader.Create(filespec, settings))
                        {
                            TrackProfile = new TrProfile(renderProcess, reader);
                        }
                        Trace.Write("(.XML)");
                        break;

                    default:
                        // File extension not supported; create a default track profile
                        TrackProfile = new TrProfile(renderProcess);
                        Trace.Write("(default)");
                        break;
                } // end switch
            } // else
        } // end TRPFile constructor

        // ValidationEventHandler callback function
        void ValidationCallback(object sender, ValidationEventArgs args)
        {
            Console.WriteLine(); // Terminate pending Write
            if (args.Severity == XmlSeverityType.Warning)
            {
                Console.WriteLine("XML VALIDATION WARNING:");
            }
            if (args.Severity == XmlSeverityType.Error)
            {
                Console.WriteLine("XML VALIDATION ERROR:");
            }
            Console.WriteLine("{0} (Line {1}, Position {2}):", 
                args.Exception.SourceUri, args.Exception.LineNumber, args.Exception.LinePosition);
            Console.WriteLine(args.Message);
            Console.WriteLine("----------");
        }

    } // end class TRPFile

    #endregion

    #region TrProfile

    // Dynamic track profile class
    public class TrProfile
    {
        #region Class Variables
        RenderProcess RenderProcess;
        string RoutePath;

        public string Name; // e.g., "Default track profile"
        public int ReplicationPitch; //TBD: Replication pitch alternative
        public LODMethods LODMethod = LODMethods.None; // LOD method of control
        public float ChordSpan; // Base method: No. of profiles generated such that span is ChordSpan degrees
        // If a PitchControl is defined, then the base method is compared to the PitchControl method,
        // and the ChordSpan is adjusted to compensate.
        public PitchControls PitchControl = PitchControls.None; // Method of control for profile replication pitch
        public float PitchControlScalar; // Scalar parameter for PitchControls
        public ArrayList LODs = new ArrayList(); // Array of Levels-Of-Detail
        #endregion

        #region Enumerations
        /// <summary>
        /// Enumeration of LOD control methods
        /// </summary>
        public enum LODMethods
        {
            /// <summary>
            /// None -- No LODMethod specified; defaults to ComponentAdditive.
            /// </summary>
            None = 0,

            /// <summary>
            /// ComponentAdditive -- Each LOD is a COMPONENT that is ADDED as the camera gets closer.
            /// </summary>
            ComponentAdditive = 1,

            /// <summary>
            /// CompleteReplacement -- Each LOD group is a COMPLETE model that REPLACES another as the camera moves.
            /// </summary>
            CompleteReplacement = 2
        } // end enum LODMethods

        /// <summary>
        /// Enumeration of cross section replication pitch control methods.
        /// </summary>
        public enum PitchControls
        {
            /// <summary>
            /// None -- No pitch control method specified.
            /// </summary>
            None = 0,

            /// <summary>
            /// ChordLength -- Constant length of chord.
            /// </summary>
            ChordLength,

            /// <summary>
            /// Chord Displacement -- Constant maximum displacement of chord from arc.
            /// </summary>
            ChordDisplacement
        } // end enum PitchControls

        #endregion

        #region TrProfile Constructors

        /// <summary>
        /// TrProfile constructor (default - builds from self-contained data)
        /// <param name="renderProcess">RenderProcess.</param>
        /// </summary>
        public TrProfile(RenderProcess renderProcess) 
        {
            // Default TrProfile constructor
            RenderProcess = renderProcess;
            RoutePath = renderProcess.Viewer.Simulator.RoutePath;

            Name = "Default Dynatrack profile";
            LODMethod = LODMethods.ComponentAdditive;
            ChordSpan = 1.0f; // Base Method: Generates profiles spanning no more than 1 degree

            PitchControl = PitchControls.ChordLength;       // Target chord length
            PitchControlScalar = 10.0f;                     // Hold to no more than 10 meters
            //PitchControl = PitchControls.ChordDisplacement; // Target chord displacement from arc
            //PitchControlScalar = 0.034f;                    // Hold to no more than 34 mm (half rail width)

            LOD lod;            // Local LOD instance
            LODItem lodItem;    // Local LODItem instance
            Polyline pl;        // Local Polyline instance

            // RAILSIDES
            lod = new LOD(700.0f); // Create LOD for railsides with specified CutoffRadius
            lodItem = new LODItem("Railsides");
            lodItem.TexName = "acleantrack2.ace";
            lodItem.ShaderName = "TexDiff";
            lodItem.LightModelName = "OptSpecular0";
            lodItem.AlphaTestMode = 0;
            lodItem.TexAddrModeName = "Wrap";
            lodItem.ESD_Alternative_Texture = 0;
            lodItem.MipMapLevelOfDetailBias = 0;
            lodItem.LoadMaterial(RenderProcess, lodItem);

            pl = new Polyline(this, "left_outer", 2);
            pl.DeltaTexCoord = new Vector2(.1673372f, 0f);
            pl.Vertices.Add(new Vertex(-.8675f, .200f, 0.0f, -1f, 0f, 0f, -.139362f, .101563f));
            pl.Vertices.Add(new Vertex(-.8675f, .325f, 0.0f, -1f, 0f, 0f, -.139363f, .003906f));
            lodItem.Polylines.Add(pl);
            lodItem.Accum(pl.Vertices.Count);

            pl = new Polyline(this, "left_inner", 2);
            pl.DeltaTexCoord = new Vector2(.1673372f, 0f);
            pl.Vertices.Add(new Vertex(-.7175f, .325f, 0.0f, 1f, 0f, 0f, -.139363f, .003906f));
            pl.Vertices.Add(new Vertex(-.7175f, .200f, 0.0f, 1f, 0f, 0f, -.139362f, .101563f));
            lodItem.Polylines.Add(pl);
            lodItem.Accum(pl.Vertices.Count);

            pl = new Polyline(this, "right_inner", 2);
            pl.DeltaTexCoord = new Vector2(.1673372f, 0f);
            pl.Vertices.Add(new Vertex(.7175f, .200f, 0.0f, -1f, 0f, 0f, -.139362f, .101563f));
            pl.Vertices.Add(new Vertex(.7175f, .325f, 0.0f, -1f, 0f, 0f, -.139363f, .003906f));
            lodItem.Polylines.Add(pl);
            lodItem.Accum(pl.Vertices.Count);

            pl = new Polyline(this, "right_outer", 2);
            pl.DeltaTexCoord = new Vector2(.1673372f, 0f);
            pl.Vertices.Add(new Vertex(.8675f, .325f, 0.0f, 1f, 0f, 0f, -.139363f, .003906f));
            pl.Vertices.Add(new Vertex(.8675f, .200f, 0.0f, 1f, 0f, 0f, -.139362f, .101563f));
            lodItem.Polylines.Add(pl);
            lodItem.Accum(pl.Vertices.Count);

            lod.LODItems.Add(lodItem); // Append this LODItem to LODItems array
            LODs.Add(lod); // Append this LOD to LODs array
            
            // RAILTOPS
            lod = new LOD(1200.0f); // Create LOD for railtops with specified CutoffRadius
            // Single LODItem in this case
            lodItem = new LODItem("Railtops");
            lodItem.TexName = "acleantrack2.ace";
            lodItem.ShaderName = "TexDiff";
            lodItem.LightModelName = "OptSpecular25";
            lodItem.AlphaTestMode = 0;
            lodItem.TexAddrModeName = "Wrap";
            lodItem.ESD_Alternative_Texture = 0;
            lodItem.MipMapLevelOfDetailBias = 0;
            lodItem.LoadMaterial(RenderProcess, lodItem);

            pl = new Polyline(this, "right", 2);
            pl.DeltaTexCoord = new Vector2(.0744726f, 0f);
            pl.Vertices.Add(new Vertex(-.8675f, .325f, 0.0f, 0f, 1f, 0f, .232067f, .126953f));
            pl.Vertices.Add(new Vertex(-.7175f, .325f, 0.0f, 0f, 1f, 0f, .232067f, .224609f));
            lodItem.Polylines.Add(pl);
            lodItem.Accum(pl.Vertices.Count);
   
            pl = new Polyline(this, "left", 2);
            pl.DeltaTexCoord = new Vector2(.0744726f, 0f);
            pl.Vertices.Add(new Vertex(.7175f, .325f, 0.0f, 0f, 1f, 0f, .232067f, .126953f));
            pl.Vertices.Add(new Vertex(.8675f, .325f, 0.0f, 0f, 1f, 0f, .232067f, .224609f));
            lodItem.Polylines.Add(pl);
            lodItem.Accum(pl.Vertices.Count);

            lod.LODItems.Add(lodItem); // Append this LODItem to LODItems array
            LODs.Add(lod); // Append this LOD to LODs array

            // BALLAST
            lod = new LOD(2000.0f); // Create LOD for ballast with specified CutoffRadius
            // Single LODItem in this case
            lodItem = new LODItem("Ballast");
            lodItem.TexName = "acleantrack1.ace";
            lodItem.ShaderName = "BlendATexDiff";
            lodItem.LightModelName = "OptSpecular0";
            lodItem.AlphaTestMode = 0;
            lodItem.TexAddrModeName = "Wrap";
            lodItem.ESD_Alternative_Texture = 1;
            lodItem.MipMapLevelOfDetailBias = -1f;
            lodItem.LoadMaterial(RenderProcess, lodItem);

            pl = new Polyline(this, "ballast", 2);
            pl.DeltaTexCoord = new Vector2(0.0f, 0.2088545f);
            pl.Vertices.Add(new Vertex(-2.5f, 0.2f, 0.0f, 0f, 1f, 0f, -.153916f, -.280582f));
            pl.Vertices.Add(new Vertex(2.5f, 0.2f, 0.0f, 0f, 1f, 0f, .862105f, -.280582f));
            lodItem.Polylines.Add(pl);
            lodItem.Accum(pl.Vertices.Count);

            lod.LODItems.Add(lodItem); // Append this LODItem to LODItems array
            LODs.Add(lod); // Append this LOD to LODs array

        } // end TrProfile() default constructor

        /// <summary>
        /// TrProfile constructor from STFReader-style profile file
        /// </summary>
        public TrProfile(RenderProcess renderProcess, STFReader stf)
        {
            Name = "Default Dynatrack profile";

            stf.MustMatch("(");
            stf.ParseBlock(new STFReader.TokenProcessor[] {
                new STFReader.TokenProcessor("name", ()=>{ Name = stf.ReadStringBlock(null); }),
                new STFReader.TokenProcessor("lodmethod", ()=> { LODMethod = GetLODMethod(stf.ReadStringBlock(null)); }),
                new STFReader.TokenProcessor("chordspan", ()=>{ ChordSpan = stf.ReadFloatBlock(STFReader.UNITS.Distance, null); }),
                new STFReader.TokenProcessor("pitchcontrol", ()=> { PitchControl = GetPitchControl(stf.ReadStringBlock(null)); }),
                new STFReader.TokenProcessor("pitchcontrolscalar", ()=>{ PitchControlScalar = stf.ReadFloatBlock(STFReader.UNITS.Distance, null); }),
                new STFReader.TokenProcessor("lod", ()=> { LODs.Add(new LOD(renderProcess, stf)); }),
            });

            if (LODs.Count == 0) throw new Exception("missing LODs");

        } // end TrProfile(STFReader) constructor

        /// <summary>
        /// TrProfile constructor from XML profile file
        /// </summary>
        public TrProfile(RenderProcess renderProcess, XmlReader reader)
        {
            if (reader.IsStartElement())
            {
                if (reader.Name == "TrProfile")
                {
                    // root
                    Name = reader.GetAttribute("Name");
                    LODMethod = GetLODMethod(reader.GetAttribute("LODMethod"));
                    ChordSpan = float.Parse(reader.GetAttribute("ChordSpan"));
                    PitchControl = GetPitchControl(reader.GetAttribute("PitchControl"));
                    PitchControlScalar = float.Parse(reader.GetAttribute("PitchControlScalar"));
                }
                else
                {
                    //TODO: Need to handle ill-formed XML profile
                }
            }
            LOD lod = null;
            LODItem lodItem = null;
            Polyline pl = null;
            Vertex v;
            string[] s;
            char[] sep = new char[] {' '};
            while (reader.Read())
            {
                if (reader.IsStartElement())
                {
                    switch (reader.Name)
                    {
                        case "LOD":
                            lod = new LOD(float.Parse(reader.GetAttribute("CutoffRadius")));
                            LODs.Add(lod);
                            break;
                        case "LODItem":
                            lodItem = new LODItem(reader.GetAttribute("Name"));
                            lodItem.TexName = reader.GetAttribute("TexName");
                            
                            lodItem.ShaderName = reader.GetAttribute("ShaderName");
                            lodItem.LightModelName = reader.GetAttribute("LightModelName");
                            lodItem.AlphaTestMode = int.Parse(reader.GetAttribute("AlphaTestMode"));
                            lodItem.TexAddrModeName = reader.GetAttribute("TexAddrModeName");
                            lodItem.ESD_Alternative_Texture = int.Parse(reader.GetAttribute("ESD_Alternative_Texture"));
                            lodItem.MipMapLevelOfDetailBias = float.Parse(reader.GetAttribute("MipMapLevelOfDetailBias"));

                            lodItem.LoadMaterial(renderProcess, lodItem);
                            lod.LODItems.Add(lodItem);
                            break;
                        case "Polyline":
                            pl = new Polyline();
                            pl.Name = reader.GetAttribute("Name");
                            s = reader.GetAttribute("DeltaTexCoord").Split(sep);
                            pl.DeltaTexCoord = new Vector2(float.Parse(s[0]), float.Parse(s[1]));
                            lodItem.Polylines.Add(pl);
                            break;
                        case "Vertex":
                            v = new Vertex();
                            s = reader.GetAttribute("Position").Split(sep);
                            v.Position = new Vector3(float.Parse(s[0]), float.Parse(s[1]), float.Parse(s[2]));
                            s = reader.GetAttribute("Normal").Split(sep);
                            v.Normal = new Vector3(float.Parse(s[0]), float.Parse(s[1]), float.Parse(s[2]));
                            s = reader.GetAttribute("TexCoord").Split(sep);
                            v.TexCoord = new Vector2(float.Parse(s[0]), float.Parse(s[1]));
                            pl.Vertices.Add(v);
                            lodItem.NumVertices++; // Bump vertex count
                            if (pl.Vertices.Count > 1) lodItem.NumSegments++;
                            break;
                        default:
                            break;
                    }
                }
            }
            if (LODs.Count == 0) throw new Exception("missing LODs");
        } // end TrProfile(XmlReader) constructor

        /// <summary>
        /// TrProfile constructor (default - builds from self-contained data)
        /// <param name="renderProcess">RenderProcess.</param>
        /// <param name="x">Parameter x is a placeholder.</param>
        /// </summary>
        public TrProfile(RenderProcess renderProcess, int x)
        {
            // Default TrProfile constructor
            RenderProcess = renderProcess;
            RoutePath = renderProcess.Viewer.Simulator.RoutePath;
            Name = "Default Dynatrack profile";
        } // end TrProfile() constructor for inherited class

        #endregion

        #region TrProfile Helpers
        /// <summary>
        /// Gets a member of the LODMethods enumeration that corresponds to sLODMethod.
        /// </summary>
        /// <param name="sLODMethod">String that identifies desired LODMethod.</param>
        /// <returns>LODMethod</returns>
        public LODMethods GetLODMethod(string sLODMethod)
        {
            string s = sLODMethod.ToLower();
            switch (s)
            {
                case "none":
                    return LODMethods.None;

                case "completereplacement":
                    return LODMethods.CompleteReplacement;

                case "componentadditive":
                default:
                    return LODMethods.ComponentAdditive;
            }
        } // end GetLODMethod

        /// <summary>
        /// Gets a member of the PitchControls enumeration that corresponds to sPitchControl.
        /// </summary>
        /// <param name="sPitchControl">String that identifies desired PitchControl.</param>
        /// <returns></returns>
        public PitchControls GetPitchControl(string sPitchControl)
        {
            string s = sPitchControl.ToLower();
            switch (s)
            {
                case "chordlength":
                    return PitchControls.ChordLength;

                case "chorddisplacement":
                    return PitchControls.ChordDisplacement;

                case "none":
                default:
                    return PitchControls.None; ;

            }
        } // end GetPitchControl
        #endregion

    } // end class TrProfile

    #endregion

    #region LOD

    public class LOD
    {
        public float CutoffRadius; // Distance beyond which LODItem is not seen
        public ArrayList LODItems = new ArrayList(); // Array of arrays of LODItems
        public int PrimIndexStart = 0; // Start index of ShapePrimitive block for this LOD
        public int PrimIndexStop = 0;

        /// <summary>
        /// LOD class constructor
        /// </summary>
        /// <param name="cutoffRadius">Distance beyond which LODItem is not seen</param>
        public LOD(float cutoffRadius)
        {
            CutoffRadius = cutoffRadius;
        }

        public LOD(RenderProcess renderProcess, STFReader stf)
        {
            stf.MustMatch("(");
            stf.ParseBlock(new STFReader.TokenProcessor[] {
                new STFReader.TokenProcessor("cutoffradius", ()=>{ CutoffRadius = stf.ReadFloatBlock(STFReader.UNITS.Distance, null); }),
                new STFReader.TokenProcessor("loditem", ()=>{
                    LODItem lodItem = new LODItem(renderProcess, stf);
                    LODItems.Add(lodItem); // Append to Polylines array
                    }),
            });
            if (CutoffRadius == 0) throw new Exception("missing CutoffRadius");
        }
    } // end class LOD

    #endregion

    #region LODItem

    public class LODItem
    {
        #region Class Variables
        public ArrayList Polylines = new ArrayList();  // Array of arrays of vertices 
        
        public string Name;                            // e.g., "Rail sides"
        public string ShaderName;
        public string LightModelName;
        public int AlphaTestMode;
        public string TexAddrModeName;
        public int ESD_Alternative_Texture; // Equivalent to that of .sd file
        public float MipMapLevelOfDetailBias;

        public string TexName; // Texture file name
        
        public Material LODMaterial; // SceneryMaterial reference

        // NumVertices and NumSegments used for sizing vertex and index buffers
        public uint NumVertices = 0;                     // Total independent vertices in LOD
        public uint NumSegments = 0;                     // Total line segment count in LOD

        #endregion

        #region LODItem Constructors

        /// <summary>
        /// LODITem constructor (used for default and XML-style profiles)
        /// </summary>
        public LODItem(string name)
        {
            Name = name;
        } // end LODItem() constructor

        /// <summary>
        /// LODITem constructor (used for STF-style profile)
        /// </summary>
        public LODItem(RenderProcess renderProcess, STFReader stf)
        {
            stf.MustMatch("(");
            stf.ParseBlock(new STFReader.TokenProcessor[] {
                new STFReader.TokenProcessor("name", ()=>{ Name = stf.ReadStringBlock(null); }),
                new STFReader.TokenProcessor("texname", ()=>{ TexName = stf.ReadStringBlock(null); }),
                new STFReader.TokenProcessor("shadername", ()=>{ ShaderName = stf.ReadStringBlock(null); }),
                new STFReader.TokenProcessor("lightmodelname", ()=>{ LightModelName = stf.ReadStringBlock(null); }),
                new STFReader.TokenProcessor("alphatestmode", ()=>{ AlphaTestMode = stf.ReadIntBlock(STFReader.UNITS.None, null); }),
                new STFReader.TokenProcessor("texaddrmodename", ()=>{ TexAddrModeName = stf.ReadStringBlock(null); }),
                new STFReader.TokenProcessor("esd_alternative_texture", ()=>{ ESD_Alternative_Texture = stf.ReadIntBlock(STFReader.UNITS.None, null); }),
                new STFReader.TokenProcessor("mipmaplevelofdetailbias", ()=>{ MipMapLevelOfDetailBias = stf.ReadFloatBlock(STFReader.UNITS.None, null); }),
                new STFReader.TokenProcessor("polyline", ()=>{
                    Polyline pl = new Polyline(stf);
                    Polylines.Add(pl); // Append to Polylines array
                    //parent.Accum(pl.Vertices.Count); }),
                    Accum(pl.Vertices.Count); }),
            });

            // Checks for required member variables:
            // Name not required.
            // MipMapLevelOfDetail bias initializes to 0.
            if (Polylines.Count == 0) throw new Exception("missing Polylines");

            LoadMaterial(renderProcess, this);
        } // end LODItem() constructor

        #endregion

        #region LODItem Helpers

        public void Accum(int count)
        {
            // Accumulates total independent vertices and total line segments
            // Used for sizing of vertex and index buffers
            NumVertices += (uint)count;
            NumSegments += (uint)count - 1;
        } // end Accum

        public void LoadMaterial(RenderProcess renderProcess, LODItem lod)
        {
            string texturePath = Helpers.GetTextureFolder(renderProcess.Viewer, lod.ESD_Alternative_Texture);
            string textureName = texturePath + @"\" + lod.TexName;
            int options = Helpers.EncodeMaterialOptions(lod); 
            lod.LODMaterial = Materials.Load(renderProcess, "SceneryMaterial", textureName, options, lod.MipMapLevelOfDetailBias);
        }

        #endregion
    } // end class LODItem

    #endregion

    #region Polyline

    public class Polyline
    {
        #region Class Variables
        public ArrayList Vertices = new ArrayList();    // Array of vertices 
 
        public string Name;                             // e.g., "1:1 embankment"
        public Vector2 DeltaTexCoord;                   // Incremental change in (u, v) from one cross section to the next

        #endregion

        #region Polyline Constructors
 
        /// <summary>
        /// Polyline constructor (DAT)
        /// </summary>
        public Polyline(STFReader stf)
        {
            stf.MustMatch("(");
            stf.ParseBlock(new STFReader.TokenProcessor[] {
                new STFReader.TokenProcessor("name", ()=>{ Name = stf.ReadStringBlock(null); }),
                new STFReader.TokenProcessor("vertex", ()=>{ Vertices.Add(new Vertex(stf)); }),
                new STFReader.TokenProcessor("deltatexcoord", ()=>{
                    stf.MustMatch("(");
                    DeltaTexCoord.X = stf.ReadFloat(STFReader.UNITS.None, null);
                    DeltaTexCoord.Y = stf.ReadFloat(STFReader.UNITS.None, null);
                    stf.SkipRestOfBlock();
                }),
            });
            // Checks for required member variables: 
            // Name not required.
            if (DeltaTexCoord == Vector2.Zero) throw new Exception("missing DeltaTexCoord");
            if (Vertices.Count == 0) throw new Exception("missing Vertices");
        } // end Polyline() constructor

        /// <summary>
        /// Bare-bones Polyline constructor (used for XML)
        /// </summary>
        public Polyline()
        {
        }

        /// <summary>
        /// Polyline constructor (default)
        /// </summary>
        public Polyline(TrProfile parent, string name, uint num)
        {
            Name = name;
        } // end Polyline() constructor

        #endregion

    } // end Polyline

    #endregion

    #region Vertex Struct

    public struct Vertex
    {
        public Vector3 Position;                           // Position vector (x, y, z)
        public Vector3 Normal;                             // Normal vector (nx, ny, nz)
        public Vector2 TexCoord;                           // Texture coordinate (u, v)

        // Vertex constructor (default)
        public Vertex(float x, float y, float z, float nx, float ny, float nz, float u, float v)
        {
            Position = new Vector3(x, y, z);
            Normal = new Vector3(nx, ny, nz);
            TexCoord = new Vector2(u, v);
        } // end Vertex() constructor

        // Vertex constructor (DAT)
        public Vertex(STFReader stf)
        {
            Vertex v = new Vertex(); // Temp variable used to construct the struct in ParseBlock
            v.Position = new Vector3();
            v.Normal = new Vector3();
            v.TexCoord = new Vector2();
            stf.MustMatch("(");
            stf.ParseBlock(new STFReader.TokenProcessor[] {
                new STFReader.TokenProcessor("position", ()=>{
                    stf.MustMatch("(");
                    v.Position.X = stf.ReadFloat(STFReader.UNITS.None, null);
                    v.Position.Y = stf.ReadFloat(STFReader.UNITS.None, null);
                    v.Position.Z = 0.0f;
                    stf.SkipRestOfBlock();
                }),
                new STFReader.TokenProcessor("normal", ()=>{
                    stf.MustMatch("(");
                    v.Normal.X = stf.ReadFloat(STFReader.UNITS.None, null);
                    v.Normal.Y = stf.ReadFloat(STFReader.UNITS.None, null);
                    v.Normal.Z = stf.ReadFloat(STFReader.UNITS.None, null);
                    stf.SkipRestOfBlock();
                }),
                new STFReader.TokenProcessor("texcoord", ()=>{
                    stf.MustMatch("(");
                    v.TexCoord.X = stf.ReadFloat(STFReader.UNITS.None, null);
                    v.TexCoord.Y = stf.ReadFloat(STFReader.UNITS.None, null);
                    stf.SkipRestOfBlock();
                }),
            });
            this = v;
            // Checks for required member variables
            // No way to check for missing Position.
            if (Normal == Vector3.Zero) throw new Exception("improper Normal");
            // No way to check for missing TexCoord
        } // end Vertex() constructor

    } // end Vertex

    #endregion

    #region DynatrackMesh
    public class DynatrackMesh : ShapePrimitive //RenderPrimitive
    {
        public ShapePrimitive[] ShapePrimitives; // Array of ShapePrimitives

		public VertexPositionNormalTexture[] VertexList; // Array of vertices
		public short[] TriangleListIndices;// Array of indices to vertices for triangles
        public uint VertexIndex = 0;       // Index of current position in VertexList
		public uint IndexIndex = 0;        // Index of current position in TriangleListIndices
        //Provided by ShapePrimitive: int NumVertices;            // Number of vertices in the track profile
		public short NumIndices;           // Number of triangle indices

        // LOD member variables:
        //public int FirstIndex;       // Marks first LOD that is in-range
        public Vector3 XNAEnd;      // Location of termination-of-section (as opposed to root)
        public float ObjectRadius;  // Radius of bounding sphere
        public Vector3 MSTSLODCenter; // Center of bounding sphere

        // Geometry member variables:
		public int NumSections;            // Number of cross sections needed to make up a track section.
		public float SegmentLength;        // meters if straight; radians if circular arc
		public Vector3 DDY;                // Elevation (y) change from one cross section to next
		public Vector3 OldV;               // Deviation from centerline for previous cross section
		public Vector3 OldRadius;          // Radius vector to centerline for previous cross section

        //TODO: Candidates for re-packaging:
		public Matrix sectionRotation;     // Rotates previous profile into next profile position on curve.
		public Vector3 center;             // Center coordinates of curve radius
		public Vector3 radius;             // Radius vector to cross section on curve centerline

        // This structure holds the basic geometric parameters of a DT section.
        public struct DtrackData
        {
            public int IsCurved;    // Straight (0) or circular arc (1)
            public float param1;    // Length in meters (straight) or radians (circular arc)
            public float param2;    // Radius for circular arc
            public float deltaY;    // Change in elevation (y) from beginning to end of section
        }
        public DtrackData DTrackData;      // Was: DtrackData[] dtrackData;

        public uint UiD; // Used for debugging only

        public TrProfile TrProfile;

        /// <summary>
        /// Default constructor
        /// </summary>
		public DynatrackMesh()
		{
		}

        /// <summary>
        /// Constructor.
        /// </summary>
        public DynatrackMesh(RenderProcess renderProcess, DyntrackObj dtrack, WorldPosition worldPosition, 
                                WorldPosition endPosition)
        {
            // DynatrackMesh is responsible for creating a mesh for a section with a single subsection.
            // It also must update worldPosition to reflect the end of this subsection, subsequently to
            // serve as the beginning of the next subsection.

            UiD = dtrack.trackSections[0].UiD; // Used for debugging only

            // The track cross section (profile) vertex coordinates are hard coded.
            // The coordinates listed here are those of default MSTS "A1t" track.
            // TODO: Read this stuff from a file. Provide the ability to use alternative profiles.

            // In this implementation dtrack has only 1 DT subsection.
            if (dtrack.trackSections.Count != 1)
            {
                throw new ApplicationException(
                    "DynatrackMesh Constructor detected a multiple-subsection dynamic track section. " +
                    "(SectionIdx = " + dtrack.SectionIdx + ")");
            }
            // Populate member DTrackData (a DtrackData struct)
            DTrackData.IsCurved = (int)dtrack.trackSections[0].isCurved;
            DTrackData.param1 = dtrack.trackSections[0].param1;
            DTrackData.param2 = dtrack.trackSections[0].param2;
            DTrackData.deltaY = dtrack.trackSections[0].deltaY;

            XNAEnd = endPosition.XNAMatrix.Translation;

            TrProfile = renderProcess.Viewer.Simulator.TRP.TrackProfile;
            // Count all of the LODItems in all the LODs
            int count = 0;
            for (int i = 0; i < TrProfile.LODs.Count; i++)
            {
                LOD lod = (LOD)TrProfile.LODs[i];
                count += lod.LODItems.Count;
            }
            // Allocate ShapePrimitives array for the LOD count
            ShapePrimitives = new ShapePrimitive[count];

            // Build the meshes for all the LODs, filling the vertex and triangle index buffers.
            int primIndex = 0;
            for (int iLOD = 0; iLOD < TrProfile.LODs.Count; iLOD++)
            {
                LOD lod = (LOD)TrProfile.LODs[iLOD];
                lod.PrimIndexStart = primIndex; // Store start index for this LOD
                for (int iLODItem = 0; iLODItem < lod.LODItems.Count; iLODItem++)
                {
                    // Build vertexList and triangleListIndices
                    ShapePrimitives[primIndex] = BuildMesh(renderProcess.Viewer, worldPosition, iLOD, iLODItem);
                    primIndex++;
                }
                lod.PrimIndexStop = primIndex; // 1 above last index for this LOD
            }

            if (DTrackData.IsCurved == 0) ObjectRadius = 0.5f * DTrackData.param1; // half-length
            else ObjectRadius = DTrackData.param2 * (float)Math.Sin(0.5 * Math.Abs(DTrackData.param1)); // half chord length

        } // end DynatrackMesh constructor

        #region Vertex and triangle index generators
        /// <summary>
        /// Builds a Dynatrack LOD to TrProfile specifications as one vertex buffer and one index buffer.
        /// The order in which the buffers are built reflects the nesting in the TrProfile.  The nesting order is:
        /// (Polylines (Vertices)).  All vertices and indices are built contiguously for an LOD.
        /// </summary>
        /// <param name="viewer">Viewer.</param>
        /// <param name="worldPosition">WorldPosition.</param>
        /// <param name="iLOD">Index of LOD mesh to be generated from profile.</param>
        /// <param name="iLODItem">Index of LOD mesh following LODs[iLOD]</param>
        public ShapePrimitive BuildMesh(Viewer3D viewer, WorldPosition worldPosition, int iLOD, int iLODItem)
        {
            // Call for track section to initialize itself
            if (DTrackData.IsCurved == 0) LinearGen();
            else CircArcGen();

            // Count vertices and indices
            LOD lod = (LOD)TrProfile.LODs[iLOD];
            LODItem lodItem = (LODItem)lod.LODItems[iLODItem];
            NumVertices = (int)(lodItem.NumVertices * (NumSections + 1));
            NumIndices = (short)(lodItem.NumSegments * NumSections * 6);
            // (Cells x 2 triangles/cell x 3 indices/triangle)

            // Allocate memory for vertices and indices
            VertexList = new VertexPositionNormalTexture[NumVertices]; // numVertices is now aggregate
            TriangleListIndices = new short[NumIndices]; // as is NumIndices

            // Build the mesh for lod
            VertexIndex = 0;
            IndexIndex = 0;
            // Initial load of baseline cross section polylines for this LOD only:
            foreach (Polyline pl in lodItem.Polylines)
            {
                foreach (Vertex v in pl.Vertices)
                {
                    VertexList[VertexIndex].Position = v.Position;
                    VertexList[VertexIndex].Normal = v.Normal;
                    VertexList[VertexIndex].TextureCoordinate = v.TexCoord;
                    VertexIndex++;
                }
            }
            // Initial load of base cross section complete

            // Now generate and load subsequent cross sections
            OldRadius = -center;
            uint stride = VertexIndex;
            for (uint i = 0; i < NumSections; i++)
            {
                foreach (Polyline pl in lodItem.Polylines)
                {
                    uint plv = 0; // Polyline vertex index
                    foreach (Vertex v in pl.Vertices)
                    {
                        if (DTrackData.IsCurved == 0) LinearGen(stride, pl); // Generation call
                        else CircArcGen(stride, pl);

                        if (plv > 0)
                        {
                            // Sense for triangles is clockwise
                            // First triangle:
                            TriangleListIndices[IndexIndex++] = (short)VertexIndex;
                            TriangleListIndices[IndexIndex++] = (short)(VertexIndex - 1 - stride);
                            TriangleListIndices[IndexIndex++] = (short)(VertexIndex - 1);
                            // Second triangle:
                            TriangleListIndices[IndexIndex++] = (short)VertexIndex;
                            TriangleListIndices[IndexIndex++] = (short)(VertexIndex - stride);
                            TriangleListIndices[IndexIndex++] = (short)(VertexIndex - 1 - stride);
                        }
                        VertexIndex++;
                        plv++;
                    } // end foreach v  
                } // end foreach pl
                OldRadius = radius; // Get ready for next segment
            } // end for i

            // Create and populate a new ShapePrimitive
            ShapePrimitive shapePrimitive = new ShapePrimitive();
            shapePrimitive.Material = lodItem.LODMaterial;
            shapePrimitive.Hierarchy = new int[1];
            shapePrimitive.Hierarchy[0] = -1;
            shapePrimitive.iHierarchy = 0;
            shapePrimitive.MinVertex = 0;
            shapePrimitive.NumVertices = NumVertices;
            shapePrimitive.IndexCount = NumIndices;
            shapePrimitive.VertexBufferSet = new SharedShape.VertexBufferSet(VertexList, viewer.GraphicsDevice);
            shapePrimitive.IndexBuffer = new IndexBuffer(viewer.GraphicsDevice, typeof(short), 
                                                            NumIndices, BufferUsage.WriteOnly);
            shapePrimitive.IndexBuffer.SetData(TriangleListIndices);
            return shapePrimitive;
        } // end BuildMesh

        /// <summary>
        /// Initializes member variables for straight track sections.
        /// </summary>
        void LinearGen()
        {
            // Define the number of track cross sections in addition to the base.
            NumSections = 1;
            //numSections = 10; //TESTING
            // TODO: Generalize count to profile file specification

            SegmentLength = DTrackData.param1 / NumSections; // Length of each mesh segment (meters)
            DDY = new Vector3(0, DTrackData.deltaY / NumSections, 0); // Incremental elevation change
        } // end LinearGen

        /// <summary>
        /// Initializes member variables for circular arc track sections.
        /// </summary>
        void CircArcGen()
        {
            // Define the number of track cross sections in addition to the base.
            NumSections = (int)(Math.Abs(MathHelper.ToDegrees(DTrackData.param1)) / TrProfile.ChordSpan);
            if (NumSections == 0) NumSections++; // Very small radius track - zero avoidance

            // Use pitch control methods
            switch (TrProfile.PitchControl)
            {
                case TrProfile.PitchControls.None:
                    break; // Good enough
                case TrProfile.PitchControls.ChordLength:
                    // Calculate chord length for NumSections
                    float l = 2.0f * DTrackData.param2 * (float)Math.Sin(0.5f * Math.Abs(DTrackData.param1) / NumSections);
                    if (l > TrProfile.PitchControlScalar)
                    {
                        // Number of sections determined by chord length of PitchControlScalar meters
                        float chordAngle = 2.0f * (float)Math.Asin(0.5f * TrProfile.PitchControlScalar / DTrackData.param2);
                        NumSections = (int)Math.Abs((DTrackData.param1 / chordAngle));
                    }
                    break;
                case TrProfile.PitchControls.ChordDisplacement:
                    // Calculate chord displacement for NumSections
                    float d = DTrackData.param2 * (float)(1.0f - Math.Cos(0.5f * Math.Abs(DTrackData.param1) / NumSections));
                    if (d > TrProfile.PitchControlScalar)
                    {
                        // Number of sections determined by chord displacement of PitchControlScalar meters
                        float chordAngle = 2.0f * (float)Math.Acos(1.0f - TrProfile.PitchControlScalar / DTrackData.param2);
                        NumSections = (int)Math.Abs((DTrackData.param1 / chordAngle));
                    }
                    break;
            }

            SegmentLength = DTrackData.param1 / NumSections; // Length of each mesh segment (radians)
            DDY = new Vector3(0, DTrackData.deltaY / NumSections, 0); // Incremental elevation change

            // The approach here is to replicate the previous cross section, 
            // rotated into its position on the curve and vertically displaced if on grade.
            // The local center for the curve lies to the left or right of the local origin and ON THE BASE PLANE
            center = DTrackData.param2 * (DTrackData.param1 < 0 ? Vector3.Left : Vector3.Right);
            sectionRotation = Matrix.CreateRotationY(-SegmentLength); // Rotation per iteration (constant)
        } // end CircArcGen

        /// <summary>
        /// Generates vertices for a succeeding cross section (straight track).
        /// </summary>
        /// <param name="stride">Index increment between section-to-section vertices.</param>
        /// <param name="pl">Polyline.</param>
        public void LinearGen(uint stride, Polyline pl)
        {
            Vector3 displacement = new Vector3(0, 0, -SegmentLength) + DDY;
            float wrapLength = displacement.Length();
            Vector2 uvDisplacement = pl.DeltaTexCoord * wrapLength;

            Vector3 p = VertexList[VertexIndex - stride].Position + displacement;
            Vector3 n = VertexList[VertexIndex - stride].Normal;
            Vector2 uv = VertexList[VertexIndex - stride].TextureCoordinate + uvDisplacement; 

            VertexList[VertexIndex].Position = new Vector3(p.X, p.Y, p.Z);
            VertexList[VertexIndex].Normal = new Vector3(n.X, n.Y, n.Z);
            VertexList[VertexIndex].TextureCoordinate = new Vector2(uv.X, uv.Y);
        }

        /// <summary>
        /// /// Generates vertices for a succeeding cross section (circular arc track).
        /// </summary>
        /// <param name="stride">Index increment between section-to-section vertices.</param>
        /// <param name="pl">Polyline.</param>
        public void CircArcGen(uint stride, Polyline pl)
        {
            // Get the previous vertex about the local coordinate system
            OldV = VertexList[VertexIndex - stride].Position - center - OldRadius;
            // Rotate the old radius vector to become the new radius vector
            radius = Vector3.Transform(OldRadius, sectionRotation);
            float wrapLength = (radius - OldRadius).Length(); // Wrap length is centerline chord
            Vector2 uvDisplacement = pl.DeltaTexCoord * wrapLength;

            // Rotate the point about local origin and reposition it (including elevation change)
            Vector3 p = DDY + center + radius + Vector3.Transform(OldV, sectionRotation);
            Vector3 n = VertexList[VertexIndex - stride].Normal;
            Vector2 uv = VertexList[VertexIndex - stride].TextureCoordinate + uvDisplacement; 

            VertexList[VertexIndex].Position = new Vector3(p.X, p.Y, p.Z);
            VertexList[VertexIndex].Normal = new Vector3(n.X, n.Y, n.Z);
            VertexList[VertexIndex].TextureCoordinate = new Vector2(uv.X, uv.Y);
        }

        #endregion

    }
    #endregion

    #endregion
}
