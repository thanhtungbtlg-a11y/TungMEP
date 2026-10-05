# -*- coding: utf-8 -*-
"""Safe optional bridge between pyRevit and the TungMEP C# payload."""

import os

from System import Activator, AppDomain, Environment
from System.IO import Directory, File, Path
from System.Reflection import Assembly

from pyrevit import DB, UI
from pyrevit.loader import sessionmgr


PAYLOAD_TYPE_PREFIX = "TungMEP.Modules."
LOADER_ASSEMBLY_NAME = "TungMEP.Loader"
LAST_COMMAND_FILE = "LastCommand.txt"


def _appdata():
    return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)


def _localappdata():
    return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)


def settings_directory():
    return Path.Combine(_appdata(), "TungMEP")


def history_path():
    return Path.Combine(settings_directory(), LAST_COMMAND_FILE)


def install_root():
    return Path.Combine(_localappdata(), "TungMEP", "Revit2024")


def payload_directory():
    return Path.Combine(install_root(), "payload")


def _is_allowed_command(command_type_name):
    return (
        command_type_name
        and command_type_name.startswith(PAYLOAD_TYPE_PREFIX)
        and "\r" not in command_type_name
        and "\n" not in command_type_name
    )


def load_last_command():
    try:
        path = history_path()
        if not File.Exists(path):
            return ""
        command_type_name = File.ReadAllText(path).strip()
        return command_type_name if _is_allowed_command(command_type_name) else ""
    except Exception:
        return ""


def save_last_command(command_type_name):
    if not _is_allowed_command(command_type_name):
        return False
    try:
        Directory.CreateDirectory(settings_directory())
        File.WriteAllText(history_path(), command_type_name.strip())
        return True
    except Exception:
        return False


def resolve_payload_path():
    directory = payload_directory()
    current_path = Path.Combine(directory, "current.txt")
    try:
        if File.Exists(current_path):
            file_name = File.ReadAllText(current_path).strip()
            if file_name and Path.GetFileName(file_name) == file_name:
                candidate = Path.Combine(directory, file_name)
                if File.Exists(candidate):
                    return candidate
        if Directory.Exists(directory):
            candidates = list(Directory.GetFiles(directory, "TungMEP.Payload.*.dll"))
            if candidates:
                candidates.sort(key=lambda path: File.GetLastWriteTimeUtc(path), reverse=True)
                return candidates[0]
    except Exception:
        pass
    return ""


def _same_path(left, right):
    try:
        return os.path.normcase(os.path.abspath(left)) == os.path.normcase(os.path.abspath(right))
    except Exception:
        return False


def get_payload_assembly(load_if_needed=True):
    payload_path = resolve_payload_path()
    if not payload_path:
        return None
    for assembly in AppDomain.CurrentDomain.GetAssemblies():
        try:
            if assembly.Location and _same_path(assembly.Location, payload_path):
                return assembly
        except Exception:
            pass
    if load_if_needed:
        try:
            return Assembly.LoadFrom(payload_path)
        except Exception:
            return None
    return None


def get_loader_assembly():
    for assembly in AppDomain.CurrentDomain.GetAssemblies():
        try:
            if assembly.GetName().Name == LOADER_ASSEMBLY_NAME:
                return assembly
        except Exception:
            pass
    return None


def _execute_external_command(command_type):
    command = Activator.CreateInstance(command_type)
    return command.Execute(
        sessionmgr.create_tmp_commanddata(),
        "",
        DB.ElementSet(),
    )


def execute_payload_command(command_type_name, remember=True):
    if not _is_allowed_command(command_type_name):
        raise ValueError("Unsafe or invalid TungMEP command type name.")
    assembly = get_payload_assembly(True)
    if assembly is None:
        raise RuntimeError(
            "The current TungMEP payload could not be found or loaded. "
            "Install/deploy TungMEP first."
        )
    command_type = assembly.GetType(command_type_name, False)
    if command_type is None:
        raise RuntimeError(
            "The current TungMEP payload does not contain: " + command_type_name
        )
    result = _execute_external_command(command_type)
    if remember and result != UI.Result.Failed:
        save_last_command(command_type_name)
    return result


def execute_loader_command(command_type_name):
    assembly = get_loader_assembly()
    if assembly is None:
        raise RuntimeError(
            "TungMEP.Loader is not loaded in this Revit session. Restart Revit after installation."
        )
    command_type = assembly.GetType(command_type_name, False)
    if command_type is None:
        raise RuntimeError(
            "The loaded TungMEP loader is older and does not contain: " + command_type_name
        )
    return _execute_external_command(command_type)


def repeat_last_command():
    command_type_name = load_last_command()
    if not command_type_name:
        raise RuntimeError(
            "No previous TungMEP command has been recorded. Run a TungMEP tool once first."
        )
    return execute_payload_command(command_type_name, remember=False)


def reload_payload():
    return execute_loader_command("TungMEP.Loader.ReloadPayloadCommand")


def diagnostics():
    payload_path = resolve_payload_path()
    loader = get_loader_assembly()
    payload = get_payload_assembly(False)
    return {
        "install_root": install_root(),
        "payload_path": payload_path,
        "payload_exists": bool(payload_path and File.Exists(payload_path)),
        "payload_loaded": payload is not None,
        "loader_loaded": loader is not None,
        "loader_version": (
            str(loader.GetName().Version) if loader is not None else "Not loaded"
        ),
        "last_command": load_last_command() or "None",
        "history_path": history_path(),
    }
