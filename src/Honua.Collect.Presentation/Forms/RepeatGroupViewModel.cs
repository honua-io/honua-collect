using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Forms;

/// <summary>View-model for one captured row of a repeatable section.</summary>
public sealed class RepeatInstanceViewModel : ObservableObject
{
    internal RepeatInstanceViewModel(RepeatInstance instance, Action onChanged)
    {
        InstanceId = instance.InstanceId;
        Fields = instance.Fields
            .Select(state => new FieldViewModel(instance, state.FieldId, onChanged))
            .ToList();
    }

    /// <summary>Stable row identifier.</summary>
    public string InstanceId { get; }

    /// <summary>Field view-models for the row.</summary>
    public IReadOnlyList<FieldViewModel> Fields { get; }

    /// <summary>Refreshes every field in the row.</summary>
    public void Refresh()
    {
        foreach (var field in Fields)
        {
            field.Refresh();
        }
    }
}

/// <summary>
/// View-model for a repeatable section on a capture screen: the list of captured
/// rows plus add/remove commands (Survey123 "repeat" / Fulcrum "repeatable
/// section").
/// </summary>
public sealed class RepeatGroupViewModel : ObservableObject
{
    private readonly RepeatGroup _group;
    private readonly Action _onChanged;

    internal RepeatGroupViewModel(RepeatGroup group, Action onChanged)
    {
        _group = group;
        _onChanged = onChanged;
        Instances = new ObservableCollection<RepeatInstanceViewModel>(
            group.Instances.Select(i => new RepeatInstanceViewModel(i, onChanged)));
        AddCommand = new RelayCommand(Add);
    }

    /// <summary>Section label.</summary>
    public string Label => _group.Label;

    /// <summary>Section identifier.</summary>
    public string SectionId => _group.SectionId;

    /// <summary>Captured rows.</summary>
    public ObservableCollection<RepeatInstanceViewModel> Instances { get; }

    /// <summary>Adds a new, empty row.</summary>
    public ICommand AddCommand { get; }

    /// <summary>Removes a row.</summary>
    /// <param name="instanceId">Row identifier.</param>
    public void Remove(string instanceId)
    {
        if (_group.RemoveInstance(instanceId))
        {
            var vm = Instances.FirstOrDefault(i => i.InstanceId == instanceId);
            if (vm is not null)
            {
                Instances.Remove(vm);
            }

            _onChanged();
        }
    }

    private void Add()
    {
        var instance = _group.AddInstance();
        Instances.Add(new RepeatInstanceViewModel(instance, _onChanged));
        _onChanged();
    }

    /// <summary>Refreshes every row's fields.</summary>
    public void Refresh()
    {
        foreach (var instance in Instances)
        {
            instance.Refresh();
        }
    }
}
