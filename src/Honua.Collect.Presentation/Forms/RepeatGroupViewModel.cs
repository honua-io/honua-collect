// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Forms;

public sealed class RepeatInstanceViewModel : ObservableObject
{
    internal RepeatInstanceViewModel(RepeatInstance instance, Action onChanged)
    {
        InstanceId = instance.InstanceId;
        Fields = instance.Fields
            .Select(state => new FieldViewModel(instance, state.FieldId, onChanged))
            .ToList();
    }

    public string InstanceId { get; }

    public IReadOnlyList<FieldViewModel> Fields { get; }

    public void Refresh()
    {
        foreach (var field in Fields)
        {
            field.Refresh();
        }
    }
}

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

    public string Label => _group.Label;

    public string SectionId => _group.SectionId;

    public ObservableCollection<RepeatInstanceViewModel> Instances { get; }

    public ICommand AddCommand { get; }

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

    public void Refresh()
    {
        foreach (var instance in Instances)
        {
            instance.Refresh();
        }
    }
}
