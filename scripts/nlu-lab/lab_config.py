"""lab.json: the deploy guard's trust root (spec: missing/unparseable refuses, never re-inits;
id AND pinned name must both match, name checked against the LIVE skill at deploy time)."""
from dataclasses import dataclass
import json
from pathlib import Path

class LabConfigError(RuntimeError):
    pass

@dataclass(frozen=True)
class LabConfig:
    skill_id: str
    skill_name: str
    invocation_name: str

    @classmethod
    def load(cls, path: Path) -> "LabConfig":
        if not path.exists():
            raise LabConfigError(f"lab.json missing at {path}: the lab refuses to run rather than re-init; run init-skill once")
        try:
            data = json.loads(path.read_text())
        except json.JSONDecodeError as e:
            raise LabConfigError(f"lab.json unparseable ({e}): the lab refuses rather than re-init; fix or delete and re-run init-skill")
        if not data.get("skill_id") or not data.get("skill_name"):
            raise LabConfigError("lab.json must carry both skill_id and skill_name; refusing")
        if not data.get("invocation_name"):
            raise LabConfigError("lab.json must carry invocation_name (the dedicated lab invocation, substituted into every deployed model); refusing")
        return cls(skill_id=data["skill_id"], skill_name=data["skill_name"], invocation_name=data["invocation_name"])

    def guard_deploy_target(self, skill_id: str, live_skill_name: str | None = None) -> None:
        if skill_id != self.skill_id:
            raise LabConfigError(f"refusing to deploy to {skill_id}: not the lab skill")
        if live_skill_name is not None and live_skill_name != self.skill_name:
            raise LabConfigError(
                f"skill {skill_id} is now named '{live_skill_name}', not the pinned '{self.skill_name}'; "
                "a stale id pointing at a renamed or foreign skill is refused")

    @classmethod
    def save(cls, path: Path, skill_id: str, skill_name: str, invocation_name: str) -> None:
        path.write_text(json.dumps({"skill_id": skill_id, "skill_name": skill_name, "invocation_name": invocation_name}, indent=2))
