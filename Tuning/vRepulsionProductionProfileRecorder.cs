using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// Captures the FINAL runtime state of vRepulsionHyperTuneEngine and freezes it
/// as a production profile for the later Bezier Runtime Editor integration.
///
/// This component does NOT modify vRepulsionHyperTuneEngine.
/// It reads its private serialized fields through reflection so the current
/// validated engine can remain untouched.
///
/// Intended workflow:
/// 1) Run the validated repulsion autotuner / validation on the engine.
/// 2) Ensure the engine has applied its best configuration at the end.
/// 3) Run CAPTURE FINAL REPULSION PROFILE.
/// 4) Use the generated JSON as the implementation contract for the runtime editor.
/// </summary>
public class vRepulsionProductionProfileRecorder : MonoBehaviour
{
    [Header("SOURCE ENGINE")]
    [SerializeField] private vRepulsionHyperTuneEngine sourceEngine;

    [Header("PROFILE METADATA")]
    [SerializeField] private string profileName = "RepulsionProductionProfile_BestValidated";
    [SerializeField] private string validationReference = "Best previously validated repulsion run";
    [SerializeField] private string targetRuntime = "Bezier Runtime Editor";
    [SerializeField] private bool includeTestAngles = true;
    [SerializeField] private bool verboseConsole = true;

    private const double Epsilon0 = 8.8541878128e-12;

    [Serializable]
    private sealed class PhysicalProfile
    {
        public double totalChargeCoulombs;
        public double leafLengthMeters;
        public double leafWidthMeters;
        public double leafAreaSquareMeters;
        public double halfLeafLengthMeters;
        public double coulombConstant;
        public double epsilon0;
    }

    [Serializable]
    private sealed class BemProfile
    {
        public int panelsAlongLength;
        public int panelsAcrossWidth;
        public int gaussOrder;
        public bool useAnalyticSelfPanelIntegral;
        public bool useNearPanelRefinement;
        public double nearPanelDistanceMultiplier;
        public int nearPanelSubdivisions;
    }

    [Serializable]
    private sealed class AngularProfile
    {
        public double torqueFiniteDifferenceDegrees;
        public double torqueFiniteDifferenceRadians;
        public double zeroAngleProbeDegrees;
        public double[] testAnglesDegrees;
    }

    [Serializable]
    private sealed class RobustnessProfile
    {
        public double quadratureCapacitanceTolerance;
        public double quadratureEnergyTolerance;
        public double quadratureTorqueTolerance;
        public double spatialCapacitanceTolerance;
        public double spatialEnergyTolerance;
        public double spatialTorqueTolerance;
    }

    [Serializable]
    private sealed class MechanicalProfile
    {
        public double leafMassKg;
        public double gravityMps2;
        public double mechanicalStartAngleDegrees;
        public double mechanicalDurationSeconds;
        public double mechanicalDtSeconds;
        public bool includeGravityInMechanicalCheck;
    }

    [Serializable]
    private sealed class RuntimePhysicsContract
    {
        public string chargeModel = "Known total conductor charge Q";
        public string electrostaticModel = "Distributed surface-charge BEM";
        public string capacitanceModel = "C = Q / V for the unit-potential solve";
        public string energyModel = "U = 0.5 * Q^2 / C at fixed Q";
        public string torqueModel = "tau(theta) = -dU/dtheta";
        public string productionTorqueDiscretization = "Symmetric central difference in radians";
        public string mechanicalIntegrator = "RK4, no artificial damping";
        public string angleSymmetry = "C(-theta)=C(theta), U(-theta)=U(theta), tau(-theta)=-tau(theta)";
        public string signConvention = "Positive opening angle must produce positive opening torque";
        public string runtimePolicy = "Preserve validated relations; do not introduce calibration multipliers into the physics";
    }

    [Serializable]
    private sealed class ProfileRoot
    {
        public string profileName;
        public string capturedUtc;
        public string sourceEngineClass;
        public string validationReference;
        public string targetRuntime;
        public string recorderVersion = "1.0";
        public PhysicalProfile physical;
        public BemProfile bem;
        public AngularProfile angular;
        public RobustnessProfile robustness;
        public MechanicalProfile mechanical;
        public RuntimePhysicsContract physicsContract;
    }

    [ContextMenu("CAPTURE FINAL REPULSION PROFILE")]
    public void CaptureFinalRepulsionProfile()
    {
        if (sourceEngine == null)
        {
            Debug.LogError("[RepulsionProfile] Source engine is not assigned.");
            return;
        }

        try
        {
            ProfileRoot profile = BuildProfile(sourceEngine);
            WriteArtifacts(profile);

            if (verboseConsole)
            {
                Debug.Log("============================================================");
                Debug.Log("REPULSION PRODUCTION PROFILE CAPTURED");
                Debug.Log("Profile: " + profile.profileName);
                Debug.Log("Source: " + profile.sourceEngineClass);
                Debug.Log("BEM: " + profile.bem.panelsAlongLength + "x" + profile.bem.panelsAcrossWidth +
                          " | Gauss-" + profile.bem.gaussOrder +
                          " | self=" + profile.bem.useAnalyticSelfPanelIntegral +
                          " | near=" + profile.bem.useNearPanelRefinement);
                Debug.Log("Torque step: " +
                          profile.angular.torqueFiniteDifferenceDegrees.ToString("R", CultureInfo.InvariantCulture) +
                          " deg");
                Debug.Log("Profile JSON: " + GetOutputFolder() + "/vRepulsionProductionProfile.json");
                Debug.Log("Profile CSV:  " + GetOutputFolder() + "/vRepulsionProductionProfile.csv");
                Debug.Log("Profile TXT:  " + GetOutputFolder() + "/vRepulsionProductionImplementationContract.txt");
                Debug.Log("============================================================");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[RepulsionProfile] Capture failed: " + ex);
        }
    }

    private ProfileRoot BuildProfile(vRepulsionHyperTuneEngine engine)
    {
        double totalQ = ReadField<double>(engine, "totalChargeCoulombs");
        double leafLength = ReadField<double>(engine, "leafLengthMeters");
        double leafWidth = ReadField<double>(engine, "leafWidthMeters");

        double torqueStepDeg = ReadField<double>(engine, "torqueFiniteDifferenceDegrees");
        double zeroProbeDeg = ReadField<double>(engine, "zeroAngleProbeDegrees");

        double[] testAngles = null;
        if (includeTestAngles)
            testAngles = ReadField<double[]>(engine, "testAnglesDegrees");

        return new ProfileRoot
        {
            profileName = profileName,
            capturedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            sourceEngineClass = typeof(vRepulsionHyperTuneEngine).FullName,
            validationReference = validationReference,
            targetRuntime = targetRuntime,
            physical = new PhysicalProfile
            {
                totalChargeCoulombs = totalQ,
                leafLengthMeters = leafLength,
                leafWidthMeters = leafWidth,
                leafAreaSquareMeters = leafLength * leafWidth,
                halfLeafLengthMeters = 0.5 * leafLength,
                coulombConstant = 1.0 / (4.0 * Math.PI * Epsilon0),
                epsilon0 = Epsilon0
            },
            bem = new BemProfile
            {
                panelsAlongLength = ReadField<int>(engine, "panelsAlongLength"),
                panelsAcrossWidth = ReadField<int>(engine, "panelsAcrossWidth"),
                gaussOrder = ReadField<int>(engine, "gaussOrder"),
                useAnalyticSelfPanelIntegral = ReadField<bool>(engine, "useAnalyticSelfPanelIntegral"),
                useNearPanelRefinement = ReadField<bool>(engine, "useNearPanelRefinement"),
                nearPanelDistanceMultiplier = ReadField<double>(engine, "nearPanelDistanceMultiplier"),
                nearPanelSubdivisions = ReadField<int>(engine, "nearPanelSubdivisions")
            },
            angular = new AngularProfile
            {
                torqueFiniteDifferenceDegrees = torqueStepDeg,
                torqueFiniteDifferenceRadians = torqueStepDeg * Math.PI / 180.0,
                zeroAngleProbeDegrees = zeroProbeDeg,
                testAnglesDegrees = testAngles ?? Array.Empty<double>()
            },
            robustness = new RobustnessProfile
            {
                quadratureCapacitanceTolerance = ReadField<double>(engine, "quadratureCapacitanceTolerance"),
                quadratureEnergyTolerance = ReadField<double>(engine, "quadratureEnergyTolerance"),
                quadratureTorqueTolerance = ReadField<double>(engine, "quadratureTorqueTolerance"),
                spatialCapacitanceTolerance = ReadField<double>(engine, "spatialCapacitanceTolerance"),
                spatialEnergyTolerance = ReadField<double>(engine, "spatialEnergyTolerance"),
                spatialTorqueTolerance = ReadField<double>(engine, "spatialTorqueTolerance")
            },
            mechanical = new MechanicalProfile
            {
                leafMassKg = ReadField<double>(engine, "leafMassKg"),
                gravityMps2 = ReadField<double>(engine, "gravityMps2"),
                mechanicalStartAngleDegrees = ReadField<double>(engine, "mechanicalStartAngleDegrees"),
                mechanicalDurationSeconds = ReadField<double>(engine, "mechanicalDurationSeconds"),
                mechanicalDtSeconds = ReadField<double>(engine, "mechanicalDtSeconds"),
                includeGravityInMechanicalCheck = ReadField<bool>(engine, "includeGravityInMechanicalCheck")
            },
            physicsContract = new RuntimePhysicsContract()
        };
    }

    private static T ReadField<T>(object instance, string fieldName)
    {
        FieldInfo field = typeof(vRepulsionHyperTuneEngine).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        if (field == null)
            throw new MissingFieldException(typeof(vRepulsionHyperTuneEngine).FullName, fieldName);

        object value = field.GetValue(instance);
        if (value == null)
            throw new InvalidOperationException("Field '" + fieldName + "' is null.");

        if (value is T typed)
            return typed;

        throw new InvalidCastException(
            "Field '" + fieldName + "' has type " + value.GetType().FullName +
            ", expected " + typeof(T).FullName + "."
        );
    }

    private void WriteArtifacts(ProfileRoot profile)
    {
        string folder = GetOutputFolder();
        Directory.CreateDirectory(folder);

        string jsonPath = Path.Combine(folder, "vRepulsionProductionProfile.json");
        string csvPath = Path.Combine(folder, "vRepulsionProductionProfile.csv");
        string txtPath = Path.Combine(folder, "vRepulsionProductionImplementationContract.txt");

        string json = JsonUtility.ToJson(profile, true);
        File.WriteAllText(jsonPath, json, Encoding.UTF8);

        StringBuilder csv = new StringBuilder();
        csv.AppendLine("section,parameter,value");
        AddCsv(csv, "metadata", "profile_name", profile.profileName);
        AddCsv(csv, "metadata", "captured_utc", profile.capturedUtc);
        AddCsv(csv, "metadata", "source_engine_class", profile.sourceEngineClass);
        AddCsv(csv, "metadata", "validation_reference", profile.validationReference);
        AddCsv(csv, "metadata", "target_runtime", profile.targetRuntime);

        AddCsv(csv, "physical", "total_charge_coulombs", profile.physical.totalChargeCoulombs);
        AddCsv(csv, "physical", "leaf_length_meters", profile.physical.leafLengthMeters);
        AddCsv(csv, "physical", "leaf_width_meters", profile.physical.leafWidthMeters);
        AddCsv(csv, "physical", "leaf_area_m2", profile.physical.leafAreaSquareMeters);
        AddCsv(csv, "physical", "half_leaf_length_meters", profile.physical.halfLeafLengthMeters);
        AddCsv(csv, "physical", "epsilon0", profile.physical.epsilon0);
        AddCsv(csv, "physical", "coulomb_constant", profile.physical.coulombConstant);

        AddCsv(csv, "bem", "panels_along_length", profile.bem.panelsAlongLength);
        AddCsv(csv, "bem", "panels_across_width", profile.bem.panelsAcrossWidth);
        AddCsv(csv, "bem", "gauss_order", profile.bem.gaussOrder);
        AddCsv(csv, "bem", "use_analytic_self_panel_integral", profile.bem.useAnalyticSelfPanelIntegral);
        AddCsv(csv, "bem", "use_near_panel_refinement", profile.bem.useNearPanelRefinement);
        AddCsv(csv, "bem", "near_panel_distance_multiplier", profile.bem.nearPanelDistanceMultiplier);
        AddCsv(csv, "bem", "near_panel_subdivisions", profile.bem.nearPanelSubdivisions);

        AddCsv(csv, "angular", "torque_finite_difference_degrees", profile.angular.torqueFiniteDifferenceDegrees);
        AddCsv(csv, "angular", "torque_finite_difference_radians", profile.angular.torqueFiniteDifferenceRadians);
        AddCsv(csv, "angular", "zero_angle_probe_degrees", profile.angular.zeroAngleProbeDegrees);

        AddCsv(csv, "robustness", "quadrature_capacitance_tolerance", profile.robustness.quadratureCapacitanceTolerance);
        AddCsv(csv, "robustness", "quadrature_energy_tolerance", profile.robustness.quadratureEnergyTolerance);
        AddCsv(csv, "robustness", "quadrature_torque_tolerance", profile.robustness.quadratureTorqueTolerance);
        AddCsv(csv, "robustness", "spatial_capacitance_tolerance", profile.robustness.spatialCapacitanceTolerance);
        AddCsv(csv, "robustness", "spatial_energy_tolerance", profile.robustness.spatialEnergyTolerance);
        AddCsv(csv, "robustness", "spatial_torque_tolerance", profile.robustness.spatialTorqueTolerance);

        AddCsv(csv, "mechanical", "leaf_mass_kg", profile.mechanical.leafMassKg);
        AddCsv(csv, "mechanical", "gravity_mps2", profile.mechanical.gravityMps2);
        AddCsv(csv, "mechanical", "start_angle_degrees", profile.mechanical.mechanicalStartAngleDegrees);
        AddCsv(csv, "mechanical", "duration_seconds", profile.mechanical.mechanicalDurationSeconds);
        AddCsv(csv, "mechanical", "dt_seconds", profile.mechanical.mechanicalDtSeconds);
        AddCsv(csv, "mechanical", "include_gravity", profile.mechanical.includeGravityInMechanicalCheck);

        if (includeTestAngles && profile.angular.testAnglesDegrees != null)
        {
            for (int i = 0; i < profile.angular.testAnglesDegrees.Length; i++)
            {
                AddCsv(csv, "validation", "test_angle_degrees_" + i,
                    profile.angular.testAnglesDegrees[i]);
            }
        }

        File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);
        File.WriteAllText(txtPath, BuildImplementationContract(profile), Encoding.UTF8);
    }

    private static string BuildImplementationContract(ProfileRoot p)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("vREPULSION PRODUCTION IMPLEMENTATION CONTRACT");
        sb.AppendLine("=============================================");
        sb.AppendLine("Profile: " + p.profileName);
        sb.AppendLine("Captured UTC: " + p.capturedUtc);
        sb.AppendLine("Source: " + p.sourceEngineClass);
        sb.AppendLine("Target: " + p.targetRuntime);
        sb.AppendLine();

        sb.AppendLine("1. PHYSICAL MODEL");
        sb.AppendLine("- Known total conductor charge Q.");
        sb.AppendLine("- Two-leaf thin conductor geometry.");
        sb.AppendLine("- Distributed surface-charge BEM solve.");
        sb.AppendLine("- Unit-potential solve -> C = Q_unit / V_unit.");
        sb.AppendLine("- At fixed Q: U = 0.5 * Q^2 / C.");
        sb.AppendLine("- Generalized electrostatic torque: tau = -dU/dtheta.");
        sb.AppendLine("- No artificial force multiplier or artificial damping is part of this contract.");
        sb.AppendLine();

        sb.AppendLine("2. GEOMETRY");
        sb.AppendLine("leafLengthMeters = " + p.physical.leafLengthMeters.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("leafWidthMeters  = " + p.physical.leafWidthMeters.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine();

        sb.AppendLine("3. BEM PRODUCTION PARAMETERS");
        sb.AppendLine("panelsAlongLength = " + p.bem.panelsAlongLength);
        sb.AppendLine("panelsAcrossWidth = " + p.bem.panelsAcrossWidth);
        sb.AppendLine("gaussOrder = " + p.bem.gaussOrder);
        sb.AppendLine("useAnalyticSelfPanelIntegral = " + p.bem.useAnalyticSelfPanelIntegral);
        sb.AppendLine("useNearPanelRefinement = " + p.bem.useNearPanelRefinement);
        sb.AppendLine("nearPanelDistanceMultiplier = " + p.bem.nearPanelDistanceMultiplier.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("nearPanelSubdivisions = " + p.bem.nearPanelSubdivisions);
        sb.AppendLine();

        sb.AppendLine("4. TORQUE EXTRACTION");
        sb.AppendLine("Production method = symmetric central difference.");
        sb.AppendLine("torqueFiniteDifferenceDegrees = " + p.angular.torqueFiniteDifferenceDegrees.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("torqueFiniteDifferenceRadians = " + p.angular.torqueFiniteDifferenceRadians.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("Important: keep torque extraction consistent with U(theta); do not replace it with an unvalidated higher-order stencil.");
        sb.AppendLine();

        sb.AppendLine("5. MECHANICS");
        sb.AppendLine("Integrator = RK4.");
        sb.AppendLine("leafMassKg = " + p.mechanical.leafMassKg.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("gravityMps2 = " + p.mechanical.gravityMps2.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("mechanicalStartAngleDegrees = " + p.mechanical.mechanicalStartAngleDegrees.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("mechanicalDurationSeconds = " + p.mechanical.mechanicalDurationSeconds.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("mechanicalDtSeconds = " + p.mechanical.mechanicalDtSeconds.ToString("R", CultureInfo.InvariantCulture));
        sb.AppendLine("includeGravityInMechanicalCheck = " + p.mechanical.includeGravityInMechanicalCheck);
        sb.AppendLine();

        sb.AppendLine("6. RUNTIME INTEGRATION RULE");
        sb.AppendLine("Use the profile as a frozen production baseline for the Bezier Runtime Editor.");
        sb.AppendLine("The Bezier editor should provide geometry state (leaf transform / opening angle) and physical Q.");
        sb.AppendLine("The electrostatic engine should return C(theta), U(theta), and tau(theta).");
        sb.AppendLine("The visual editor must not alter the underlying electrostatic relations.");
        sb.AppendLine();

        sb.AppendLine("7. VALIDATED RELATION CONTRACT");
        sb.AppendLine("C(-theta) = C(theta)");
        sb.AppendLine("U(-theta) = U(theta)");
        sb.AppendLine("tau(-theta) = -tau(theta)");
        sb.AppendLine("U ~ Q^2 at fixed geometry");
        sb.AppendLine("tau ~ Q^2 at fixed geometry");
        sb.AppendLine("Opening torque sign must be physically consistent with -dU/dtheta.");
        sb.AppendLine();

        sb.AppendLine("This file records the tested implementation parameters; it does not claim absolute experimental metrology.");
        return sb.ToString();
    }

    private static string GetOutputFolder()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "RepulsionValidation");
#else
        return Application.persistentDataPath;
#endif
    }

    private static void AddCsv(StringBuilder sb, string section, string parameter, object value)
    {
        string text = FormatCsvValue(value);
        sb.Append(EscapeCsv(section));
        sb.Append(',');
        sb.Append(EscapeCsv(parameter));
        sb.Append(',');
        sb.AppendLine(EscapeCsv(text));
    }

    private static string FormatCsvValue(object value)
    {
        if (value == null)
            return "";

        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);

        return value.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value == null)
            return "";

        if (value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 ||
            value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
