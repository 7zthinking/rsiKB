from pathlib import Path
import yaml

path = Path(__file__).resolve().parents[1] / ".github" / "workflows" / "build.yml"
with path.open(encoding="utf-8") as stream:
    workflow = yaml.safe_load(stream)
assert workflow.get("name") == "Build, test and package"
assert "jobs" in workflow
assert {"build", "publish-release"}.issubset(workflow["jobs"])
for job_name, job in workflow["jobs"].items():
    assert job.get("runs-on") == "windows-latest", job_name
    assert job.get("steps"), job_name
    for step in job["steps"]:
        assert step.get("uses") or step.get("run"), (job_name, step)
print(f"Workflow syntax parsed: {len(workflow['jobs'])} jobs, all steps valid.")
