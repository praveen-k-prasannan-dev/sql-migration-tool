namespace SqlMigrationTool.Models;

/// <summary>How generated scripts treat objects that already exist on the target.</summary>
public enum ScriptMode
{
    /// <summary>Drop the existing object, then create it (v1 behaviour).</summary>
    DropAndCreate,

    /// <summary>Create the object only when it does not exist; existing objects are left untouched.</summary>
    CreateOnly,

    /// <summary>Create the object when missing, otherwise alter it in place (keeps permissions).</summary>
    CreateOrAlter
}

public static class ScriptModeText
{
    public static string Label(ScriptMode mode) => mode switch
    {
        ScriptMode.DropAndCreate => "Drop and Create",
        ScriptMode.CreateOnly    => "Create Only (skip existing)",
        ScriptMode.CreateOrAlter => "Create or Alter",
        _                        => mode.ToString()
    };
}
