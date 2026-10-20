from contextlib import contextmanager
from pathlib import Path
import shutil
import subprocess
import unittest
import uuid
from xml.sax.saxutils import escape

from dependency_graph import BACKPLANE, CLIENT_PROTOCOL, validate_graph, validate_packages, validate_restore_inputs


ROOT = Path(__file__).resolve().parents[2]
TARGETS = ROOT / "scripts/architecture/dependency-boundaries.targets"


@contextmanager
def fixture_directory():
    # Plain mkdir inherits workspace ACLs on Windows sandboxed runners;
    # tempfile's restrictive mode can create an inaccessible directory there.
    directory = ROOT / "artifacts/dependency-guard-fixtures" / uuid.uuid4().hex
    directory.mkdir(parents=True)
    try:
        yield directory
    finally:
        shutil.rmtree(directory)


class GraphTests(unittest.TestCase):
    def test_cycles_are_rejected_even_when_edges_are_allowed(self):
        graph = {"A": ["B"], "B": ["A"]}
        self.assertTrue(any("Project cycle:" in error for error in validate_graph(graph, graph)))

    def test_service_reference_is_rejected_even_when_allowlisted(self):
        graph = {"ConduitLLM.Admin": ["ConduitLLM.Gateway"], "ConduitLLM.Gateway": []}
        self.assertTrue(any("service executable" in error for error in validate_graph(graph, graph)))

    def test_transitive_execution_dependency_cannot_hide_behind_adapter(self):
        graph = {"ConduitLLM.Core": ["Adapter"], "Adapter": ["ConduitLLM.Configuration"], "ConduitLLM.Configuration": []}
        self.assertTrue(any("Core -> Adapter -> ConduitLLM.Configuration" in error for error in validate_graph(graph, graph)))

    def test_neutral_project_cannot_transitively_reach_function_implementations(self):
        graph = {"ConduitLLM.Contracts": ["Adapter"], "Adapter": ["ConduitLLM.Functions"], "ConduitLLM.Functions": []}
        self.assertTrue(any("Contracts -> Adapter -> ConduitLLM.Functions" in error for error in validate_graph(graph, graph)))

    def test_new_project_requires_explicit_ownership(self):
        self.assertIn("Unclassified production project: New", validate_graph({"New": []}, {}))

    def test_production_graph_cannot_reference_uninventoried_tool(self):
        errors = validate_graph({"A": ["Migrator"]}, {"A": ["Migrator"]})
        self.assertTrue(any("leaves the inventoried graph" in error for error in errors))

    def test_neutral_execution_graph_is_valid(self):
        graph = {"ConduitLLM.Core": ["ConduitLLM.Persistence.Abstractions"], "ConduitLLM.Persistence.Abstractions": []}
        self.assertEqual([], validate_graph(graph, graph))

    def test_resolved_packages_preserve_native_redis_messagepack(self):
        self.assertEqual([], validate_packages("ConduitLLM.SignalR", [BACKPLANE, "MessagePack"], "native"))
        self.assertTrue(validate_packages("ConduitLLM.SignalR", [BACKPLANE], "native"))
        self.assertTrue(validate_packages("ConduitLLM.SignalR", [BACKPLANE, "MessagePack", CLIENT_PROTOCOL], "native"))

    def test_jit_protocol_is_required(self):
        self.assertEqual([], validate_packages("ConduitLLM.SignalR", [BACKPLANE, "MessagePack", CLIENT_PROTOCOL], "jit"))
        self.assertTrue(validate_packages("ConduitLLM.SignalR", [BACKPLANE, "MessagePack"], "jit"))

    def test_resolved_packages_reject_all_implementation_families(self):
        for package in ["Microsoft.EntityFrameworkCore.Relational", "Npgsql", "WolverineFx", "AWSSDK.S3", "Microsoft.OpenApi", "Microsoft.AspNetCore.SignalR.StackExchangeRedis"]:
            with self.subTest(package=package):
                self.assertTrue(validate_packages("ConduitLLM.Persistence.Abstractions", [package], "jit"))

    def test_host_runtime_tooling_and_admin_gateway_adapters_are_rejected(self):
        for package in ["Microsoft.EntityFrameworkCore.Design", "Microsoft.Build.Tasks.Core", "WolverineFx.RuntimeCompilation", "JasperFx.RuntimeCompiler", "Microsoft.CodeAnalysis.CSharp"]:
            with self.subTest(package=package):
                self.assertTrue(validate_packages("ConduitLLM.Admin", [package], "jit"))
        self.assertTrue(validate_packages("ConduitLLM.Admin", [CLIENT_PROTOCOL], "jit"))
        self.assertTrue(validate_packages("ConduitLLM.Admin", ["Microsoft.ML.Tokenizers"], "jit"))

    def test_stale_restore_cannot_claim_a_different_profile_or_project_graph(self):
        assets = {"project": {"frameworks": {"net10.0": {"dependencies": {CLIENT_PROTOCOL: {}}}},
                              "restore": {"frameworks": {"net10.0": {"projectReferences": {}}}}},
                  "targets": {"net10.0": {}}}
        self.assertEqual([], validate_restore_inputs("SignalR", [CLIENT_PROTOCOL], [], assets))
        self.assertTrue(validate_restore_inputs("SignalR", [], [], assets))
        self.assertTrue(validate_restore_inputs("SignalR", [CLIENT_PROTOCOL], [str(ROOT / "Other.csproj")], assets))
        self.assertTrue(validate_restore_inputs("SignalR", [CLIENT_PROTOCOL], [], assets, "linux-x64"))


class MSBuildGuardTests(unittest.TestCase):
    """Invoke the actual build targets against isolated hostile dependency items.

    Fixtures have no SDK/packages/network dependency: a synthetic resolved item
    represents the dependency the upstream resolver would hand to the guard.
    """

    def run_guard(self, project, target, items="", properties="", output_files=(), evidence=False):
        with fixture_directory() as root:
            publish = root / "publish"
            publish.mkdir()
            for leaf in output_files:
                destination = publish / leaf
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_text("fixture")
            fixture = root / f"{project}.proj"
            if evidence:
                properties += f'<ConduitDependencyEvidenceDirectory>{escape(str(root / "evidence"))}</ConduitDependencyEvidenceDirectory>'
            fixture.write_text(f'<Project><PropertyGroup><PublishDir>{escape(str(publish))}/</PublishDir>{properties}</PropertyGroup>'
                               f'<ItemGroup>{items}</ItemGroup><Import Project="{escape(str(TARGETS))}" /></Project>')
            result = subprocess.run(["dotnet", "msbuild", str(fixture), "-nologo", f"-t:{target}"], capture_output=True, text=True)
            result.evidence = {path.name: path.read_text(encoding="utf-8-sig") for path in (root / "evidence").glob("*.txt")}
            return result

    def assert_rejected(self, result, expected):
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn(expected, result.stdout + result.stderr)

    def test_resolved_execution_packages_reject_unused_private_transitive_ef(self):
        for project in ["ConduitLLM.Core", "ConduitLLM.Providers", "ConduitLLM.Functions", "ConduitLLM.Tokenization"]:
            with self.subTest(project=project):
                result = self.run_guard(project, "ConduitVerifyResolvedDependencyBoundary", '<PackageDependencies Include="Microsoft.EntityFrameworkCore.Relational" />')
                self.assert_rejected(result, "dependency boundary rejected resolved packages")

    def test_resolved_neutral_projects_reject_optional_sdks(self):
        for project in ["ConduitLLM.Contracts", "ConduitLLM.Persistence.Abstractions"]:
            for package in ["WolverineFx", "Npgsql", "AWSSDK.S3", "Microsoft.OpenApi"]:
                with self.subTest(project=project, package=package):
                    result = self.run_guard(project, "ConduitVerifyResolvedDependencyBoundary", f'<PackageDependencies Include="{package}" />')
                    self.assert_rejected(result, "dependency boundary rejected resolved packages")

    def test_unused_optional_sdks_cannot_leak_back_into_execution(self):
        for project in ["ConduitLLM.Core", "ConduitLLM.Providers"]:
            result = self.run_guard(project, "ConduitVerifyResolvedDependencyBoundary", '<PackageDependencies Include="AWSSDK.S3" />')
            self.assert_rejected(result, "dependency boundary rejected resolved packages")

    def test_publish_inputs_reject_tooling_before_native_linking(self):
        result = self.run_guard("ConduitLLM.Gateway", "ConduitVerifyRuntimeDependencyInputs", '<ReferenceCopyLocalPaths Include="pkg/Microsoft.Build.Framework.dll" />', '<PublishAot>true</PublishAot>')
        self.assert_rejected(result, "runtime/publish dependency boundary rejected inputs")

    def test_generation_opt_in_cannot_disable_publish_tooling_guard(self):
        result = self.run_guard("ConduitLLM.Gateway", "ConduitVerifyRuntimeDependencyInputs",
                                '<ResolvedFileToPublish Include="pkg/Wolverine.RuntimeCompilation.dll" />',
                                '<ConduitWolverineCodegen>true</ConduitWolverineCodegen>')
        self.assert_rejected(result, "runtime/publish dependency boundary rejected inputs")

    def test_publish_items_reject_unused_admin_adapter(self):
        result = self.run_guard("ConduitLLM.Admin", "ConduitVerifyRuntimeDependencyInputs", '<ResolvedFileToPublish Include="pkg/Microsoft.ML.Tokenizers.dll" />')
        self.assert_rejected(result, "runtime/publish dependency boundary rejected inputs")

    def test_published_files_reject_nested_tooling_leak(self):
        result = self.run_guard("ConduitLLM.Admin", "ConduitVerifyPublishedDependencyBoundary", output_files=["tools/Microsoft.EntityFrameworkCore.Design.dll"])
        self.assert_rejected(result, "publish output dependency boundary rejected files")

    def test_native_protocol_inputs_are_rejected_but_backplane_messagepack_remains(self):
        native = '<PublishAot>true</PublishAot>'
        result = self.run_guard("ConduitLLM.Gateway", "ConduitVerifyRuntimeDependencyInputs", '<ResolvedFileToPublish Include="MessagePack.dll;Microsoft.AspNetCore.SignalR.StackExchangeRedis.dll" />', native)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        result = self.run_guard("ConduitLLM.Gateway", "ConduitVerifyRuntimeDependencyInputs", f'<ResolvedFileToPublish Include="{CLIENT_PROTOCOL}.dll" />', native)
        self.assert_rejected(result, "runtime/publish dependency boundary rejected inputs")

    def test_native_output_rejects_client_protocol_and_preserves_redis_messagepack(self):
        result = self.run_guard("ConduitLLM.Gateway", "ConduitVerifyPublishedDependencyBoundary", properties='<PublishAot>true</PublishAot>', output_files=["MessagePack.dll", f"{BACKPLANE}.dll"])
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        result = self.run_guard("ConduitLLM.Gateway", "ConduitVerifyPublishedDependencyBoundary", properties='<PublishAot>true</PublishAot>', output_files=[f"{CLIENT_PROTOCOL}.dll"])
        self.assert_rejected(result, "publish output dependency boundary rejected files")

    def test_jit_protocol_remains_allowed(self):
        result = self.run_guard("ConduitLLM.Gateway", "ConduitVerifyRuntimeDependencyInputs", f'<ResolvedFileToPublish Include="{CLIENT_PROTOCOL}.dll" />')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_publish_evidence_records_actual_inputs_outputs_and_profile(self):
        result = self.run_guard("ConduitLLM.Gateway", "ConduitVerifyRuntimeDependencyInputs;ConduitVerifyPublishedDependencyBoundary",
                                '<ResolvedFileToPublish Include="pkg/MessagePack.dll" />',
                                '<PublishAot>true</PublishAot><RuntimeIdentifier>linux-x64</RuntimeIdentifier>',
                                output_files=["ConduitLLM.Gateway", "MessagePack.dll"], evidence=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        inputs = result.evidence["ConduitLLM.Gateway-inputs.txt"]
        outputs = result.evidence["ConduitLLM.Gateway-outputs.txt"]
        self.assertIn("publishAot=true", inputs)
        self.assertIn("rid=linux-x64", inputs)
        self.assertIn("pkg/MessagePack.dll", inputs)
        self.assertIn("MessagePack.dll", outputs)
        self.assertIn("ConduitLLM.Gateway", outputs)


if __name__ == "__main__":
    unittest.main()
