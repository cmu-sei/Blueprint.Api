// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Blueprint.Api.Data.Enumerations;

namespace Blueprint.Api.Data.Models
{
    /// <summary>
    /// One automated step of a scenario event: an action Blueprint performs, against the MSEL's Player view,
    /// CITE evaluation or Gallery exhibit, the VMs of a Player view, an SSH host, a mail server or a URL.
    /// A scenario event has an ordered list of them, and a task can be triggered by another task of the MSEL.
    /// </summary>
    public class AutomationTaskEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        // the scenario event's MSEL, kept here so a MSEL's tasks are one indexed read
        public Guid MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        public Guid ScenarioEventId { get; set; }
        public virtual ScenarioEventEntity ScenarioEvent { get; set; }
        public int DisplayOrder { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public AutomationAction Action { get; set; }
        // the action's parameters, other than its secrets, by the catalog's parameter keys
        public Dictionary<string, string> ActionParameters { get; set; } = new();
        // the action's secret parameters (passwords, keys, headers), encrypted; never returned by the API
        public string SecretParameters { get; set; }
        // the VMs of the Player view the task runs on: a comma separated list of VM ids, or part of their names
        public string VmMask { get; set; }
        // the run succeeds when the action's output contains this, ignoring case; empty matches any output
        public string ExpectedOutput { get; set; }
        public int ExpirationSeconds { get; set; }
        public int DelaySeconds { get; set; }
        public int IntervalSeconds { get; set; }
        public int Iterations { get; set; } = 1;
        public AutomationIterationTermination IterationTermination { get; set; }
        public AutomationTrigger TriggerCondition { get; set; }
        public Guid? TriggerTaskId { get; set; }
        public virtual AutomationTaskEntity TriggerTask { get; set; }
        public virtual ICollection<AutomationTaskEntity> TriggeredTasks { get; set; } = new HashSet<AutomationTaskEntity>();
        public bool Enabled { get; set; } = true;
        // a task that is not repeatable runs once per deployment: it cannot be rerun once it succeeded
        public bool Repeatable { get; set; }
    }

    public class AutomationTaskEntityConfiguration : IEntityTypeConfiguration<AutomationTaskEntity>
    {
        public void Configure(EntityTypeBuilder<AutomationTaskEntity> builder)
        {
            builder
                .HasOne(t => t.ScenarioEvent)
                .WithMany(e => e.AutomationTasks)
                .HasForeignKey(t => t.ScenarioEventId)
                .OnDelete(DeleteBehavior.Cascade);
            // the scenario event's cascade removes a MSEL's tasks; a second cascade path is not allowed
            builder
                .HasOne(t => t.Msel)
                .WithMany()
                .HasForeignKey(t => t.MselId)
                .OnDelete(DeleteBehavior.NoAction);
            builder
                .HasOne(t => t.TriggerTask)
                .WithMany(t => t.TriggeredTasks)
                .HasForeignKey(t => t.TriggerTaskId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasIndex(t => new { t.ScenarioEventId, t.DisplayOrder });
            builder.HasIndex(t => t.MselId);
        }
    }

    /// <summary>
    /// One run of an automation task: one manual run, scheduled run, chained run, rerun or test. It keeps a copy
    /// of the task as it ran, so its history reads true after the task is edited or deleted.
    /// </summary>
    public class AutomationRunEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid MselId { get; set; }
        public virtual MselEntity Msel { get; set; }
        // null once the task is deleted, so its runs outlive it
        public Guid? AutomationTaskId { get; set; }
        public virtual AutomationTaskEntity AutomationTask { get; set; }
        public Guid? ScenarioEventId { get; set; }
        public string TaskName { get; set; }
        public AutomationAction Action { get; set; }
        // the task as it was when the run was made (JSON, secrets still encrypted)
        public string ConfigSnapshot { get; set; }
        public AutomationRunKind Kind { get; set; }
        public AutomationRunSource Source { get; set; }
        public Guid? ParentRunId { get; set; }
        public Guid? RerunOfRunId { get; set; }
        // the deployment the run belongs to (Msel.DeploymentId when it was made); null for a test
        public Guid? DeploymentId { get; set; }
        // the Player view whose VMs the run uses
        public Guid? PlayerViewId { get; set; }
        // who asked for the run; null when the scheduler or a chain made it
        public Guid? RequestedBy { get; set; }
        // whether the tasks triggered by this one run after it
        public bool TriggerChain { get; set; } = true;
        // the manual tasks a group start runs one after another, as a comma separated id list
        public string FollowOnTaskIds { get; set; }
        public AutomationStatus Status { get; set; }
        public string StatusDetail { get; set; }
        public int CurrentIteration { get; set; }
        public DateTime DueAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public bool CancelRequested { get; set; }
        // the API instance working the run, and until when; another instance takes the run over after that
        public string LeaseOwner { get; set; }
        public DateTime? LeaseUntil { get; set; }
        public virtual ICollection<AutomationResultEntity> Results { get; set; } = new HashSet<AutomationResultEntity>();
    }

    public class AutomationRunEntityConfiguration : IEntityTypeConfiguration<AutomationRunEntity>
    {
        public void Configure(EntityTypeBuilder<AutomationRunEntity> builder)
        {
            builder
                .HasOne(r => r.Msel)
                .WithMany()
                .HasForeignKey(r => r.MselId)
                .OnDelete(DeleteBehavior.Cascade);
            builder
                .HasOne(r => r.AutomationTask)
                .WithMany()
                .HasForeignKey(r => r.AutomationTaskId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.Property(r => r.ConfigSnapshot).HasColumnType("jsonb");
            builder.HasIndex(r => new { r.MselId, r.Kind, r.DateCreated });
            builder.HasIndex(r => new { r.Status, r.DueAt });
            // at most one scheduled run per task and deployment, however many API instances schedule it
            builder.HasIndex(r => new { r.AutomationTaskId, r.DeploymentId })
                .IsUnique()
                .HasFilter("source = 10");
        }
    }

    /// <summary>
    /// The outcome of one iteration of a run on one VM (or of the run itself, for an action without VMs).
    /// </summary>
    public class AutomationResultEntity : BaseEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }
        public Guid RunId { get; set; }
        public virtual AutomationRunEntity Run { get; set; }
        public int Iteration { get; set; }
        public Guid? VmId { get; set; }
        public string VmName { get; set; }
        public AutomationStatus Status { get; set; }
        // the parameters the action ran with, secrets redacted (JSON)
        public string InputString { get; set; }
        public string ExpectedOutput { get; set; }
        public string ActualOutput { get; set; }
        public bool OutputTruncated { get; set; }
        public int ExpirationSeconds { get; set; }
        public DateTime? SentAt { get; set; }
        public DateTime? StatusAt { get; set; }
    }

    public class AutomationResultEntityConfiguration : IEntityTypeConfiguration<AutomationResultEntity>
    {
        public void Configure(EntityTypeBuilder<AutomationResultEntity> builder)
        {
            builder
                .HasOne(r => r.Run)
                .WithMany(r => r.Results)
                .HasForeignKey(r => r.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(r => new { r.RunId, r.Iteration });
        }
    }
}
