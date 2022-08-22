// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

namespace Blueprint.Api.Data.Enumerations
{
    public enum ItemStatus
    {
        Pending = 0,
        Entered = 10,
        Approved = 20,
        Complete = 30
    }

    public enum DataFieldType{
        String = 0,
        Integer = 10,
        Double = 20,
        Boolean = 30,
        DateTime = 40,
        Organization = 50
    }

    public enum MselRole{
        Owner = 10,
        Editor = 20,
        Approver = 30
    }

}

